﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Enums;
using SpotifyAPI.Web.Models;

namespace PugetSound
{
    public class PartyRoom
    {
        private readonly ILogger _logger;
        private DateTimeOffset _handledUntil;
        private int _currentDjNumber;
        private FullTrack _currentTrack;

        public string RoomId { get; }

        public RoomState CurrentRoomState { get; private set; }

        private readonly List<RoomMember> _members;

        public IReadOnlyCollection<RoomMember> Members => _members;

        public PartyRoom(string roomId, ILogger logger)
        {
            _logger = logger;
            RoomId = roomId;
            _members = new List<RoomMember>();

            _handledUntil = DateTimeOffset.Now;
            _currentDjNumber = -1;
            _currentTrack = null;
            CurrentRoomState = new RoomState();
        }

        public event EventHandler<string> OnRoomMembersChanged;

        public void MemberJoin(RoomMember member)
        {
            if (_members.Any(x => x.UserName == member.UserName)) return;
            _members.Add(member);
            OnRoomMembersChanged?.Invoke(this, member.UserName);

            if (_currentTrack != null) StartSongForMemberUgly(member);
        }

        private async void StartSongForMemberUgly(RoomMember member)
        {
            var left = _handledUntil.ToUnixTimeMilliseconds() - DateTimeOffset.Now.ToUnixTimeMilliseconds();
            await PlaySong(member, _currentTrack, (int) (_currentTrack.DurationMs - left));
        }

        public void ToggleDj(RoomMember member, bool isDj)
        {
            member.IsDj = isDj;

            foreach (var roomMember in _members)
            {
                if (roomMember.IsDj && roomMember.DjOrderNumber < 0) roomMember.DjOrderNumber = _members.Where(x => x.IsDj).Max(y => y.DjOrderNumber) + 1;
                if (!roomMember.IsDj && roomMember.DjOrderNumber >= 0) roomMember.DjOrderNumber = -1;
            }

            OnRoomMembersChanged?.Invoke(this, null);
        }

        public void VoteSkipSong(RoomMember member)
        {
            member.VotedSkipSong = true;

            if (_members.Count / 2 > _members.Count(x => x.VotedSkipSong)) return;

            _handledUntil = DateTimeOffset.Now;
            foreach (var roomMember in _members)
            {
                roomMember.VotedSkipSong = false;
            }
        }

        public void MemberLeave(RoomMember member)
        {
            var didRemove = _members.Remove(member);
            if (!didRemove) return;

            OnRoomMembersChanged?.Invoke(this, member.UserName);
        }

        public async Task<RoomState> TryPlayNext(bool force = false)
        {
            while (true)
            {
                // return if song is playing right now, except when we're skipping a song
                if (!force && DateTimeOffset.Now < _handledUntil)
                {
                    // we don't change room state here
                    return new RoomState();
                }

                _currentTrack = null;

                // try getting next player
                var nextPlayer = GetNextPlayer();

                // if we don't find any we don't have a DJ - this will exit the recursion if we run out of DJs
                if (nextPlayer == null)
                {
                    CurrentRoomState = new RoomState();
                    return CurrentRoomState;
                }

                var song = await GetSongFromQueue(nextPlayer, nextPlayer.PlaylistId);

                // success
                if (song != null)
                {
                    _currentDjNumber = nextPlayer.DjOrderNumber;

                    // start songs for everyone
                    foreach (var roomMember in _members)
                    {
                        await PlaySong(roomMember, song);
                    }

                    // set handled
                    _handledUntil = DateTimeOffset.Now.AddMilliseconds(song.DurationMs);
                    _currentTrack = song;

                    // return state
                    CurrentRoomState = new RoomState
                    {
                        IsPlayingSong = true,
                        CurrentDjUsername = nextPlayer.UserName,
                        CurrentDjName = nextPlayer.FriendlyName,
                        CurrentSongArtist = string.Join(", ", song.Artists.Select(x => x.Name).ToArray()),
                        CurrentSongTitle = song.Name,
                        CurrentSongArtUrl = song?.Album.Images.FirstOrDefault()?.Url ?? "/images/missingart.jpg",
                        SongStartedAtUnixTimestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                        SongFinishesAtUnixTimestamp = _handledUntil.ToUnixTimeMilliseconds()
                    };
                    return CurrentRoomState;
                }

                // remove user as DJ if song is null
                nextPlayer.IsDj = false;

                // then try again
            }
        }

        private async Task PlaySong(RoomMember member, FullTrack song, int positionMs = 0)
        {
            try
            {
                var api = member.MemberApi;
                var devices = await api.GetDevicesAsync();
                if (devices.HasError()) throw new Exception($"Device error {devices.Error.Status} - {devices.Error.Message}");
                if (!devices.Devices.Any()) throw new Exception("No devices available to play on!");

                var device = devices.Devices.FirstOrDefault(x => x.IsActive) ?? devices.Devices.First();

                var err = await api.ResumePlaybackAsync(deviceId:device.Id, uris: new List<string> { song.Uri }, offset: 0, positionMs: positionMs);
                if (err.HasError()) throw new Exception($"Playback error {err.Error.Status} - {err.Error.Message}");

                // we don't care if this one fails
                await api.SetRepeatModeAsync(RepeatState.Off, device.Id);
            }
            catch (Exception e)
            {
                _logger.Log(LogLevel.Warning, "Failed to play song for {Username} because {@Exception}", member.UserName, e);
                // oh well
                Debug.WriteLine(e);
            }
        }

        private async Task<FullTrack> GetSongFromQueue(RoomMember member, string playlist)
        {
            try
            {
                var api = member.MemberApi;

                var queueList = await api.GetPlaylistTracksAsync(playlist);

                if (!queueList.Items.Any()) return null;

                var track = queueList.Items.First().Track;

                var err = await api.RemovePlaylistTrackAsync(playlist, new DeleteTrackUri(track.Uri, 0));

                if (err.HasError()) throw new Exception("couldn't remove song from queue");

                return track;

            }
            catch (Exception e)
            {
                _logger.Log(LogLevel.Warning, "Failed to get song from {Username}'s queue because {@Exception}", member.UserName, e);
                Debug.WriteLine(e);
                return null;
            }
        }

        private RoomMember GetNextPlayer()
        {
            if (!_members.Any(x => x.IsDj)) return null;
            var orderedDjs = _members.Where(x => x.IsDj).OrderBy(y => y.DjOrderNumber).ToList();
            var nextDj = orderedDjs.FirstOrDefault(x => x.DjOrderNumber > _currentDjNumber);
            return nextDj ?? orderedDjs.First();
        }
    }
}