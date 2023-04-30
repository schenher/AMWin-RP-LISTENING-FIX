﻿using System;
using System.Diagnostics;
using System.Timers;
using System.Windows.Automation;
using System.Windows.Automation.Provider;

namespace AMWin_RichPresence {

    internal class AppleMusicInfo {
        // the following fields are only valid if HasSong is true.
        // DateTimes are in UTC.
        public string   SongName;
        public string   SongSubTitle;
        public string   SongAlbum;
        public string   SongArtist;
        public bool     IsPaused = true;
        public int?     SongDuration = null;
        public DateTime PlaybackStart = DateTime.MinValue;
        public DateTime PlaybackEnd = DateTime.MinValue;
        public string?  CoverArtUrl = null;

        public AppleMusicInfo(string songName, string songSubTitle, string songAlbum, string songArtist) {
            this.SongName = songName;
            this.SongSubTitle = songSubTitle;
            this.SongAlbum = songAlbum;
            this.SongArtist = songArtist;
        }

        public override string ToString() {
            return $"""
                [AppleMusicInfo] 
                _________________________________________________
                |  {SongName} ({(SongDuration == null ? "" : $"{SongDuration / 60}:{((int)SongDuration % 60).ToString("D2")} min")}{(IsPaused ? ", paused" : "")})
                |  by {SongArtist} on {SongAlbum},
                |------------------------------------------------
                |  Playback: {PlaybackStart} to {PlaybackEnd},
                |  Cover Art URL: {CoverArtUrl},
                -------------------------------------------------
                """;
        }
        public void Print() {
            Trace.WriteLine(ToString());
        }
    }

    internal class AppleMusicClientScraper {

        public delegate void RefreshHandler(AppleMusicInfo? newInfo);
     
        Timer timer;
        RefreshHandler refreshHandler;
        AppleMusicInfo? currentSong;

        public AppleMusicClientScraper(int refreshPeriodInSec, RefreshHandler refreshHandler) {
            this.refreshHandler = refreshHandler;
            timer = new Timer(refreshPeriodInSec * 1000);
            timer.Elapsed += Refresh;
            Refresh(this, null);
            timer.Start();
        }

        public void Refresh(object? source, ElapsedEventArgs? e) {
            AppleMusicInfo? appleMusicInfo = null;
            AutomationElement? appleMusicWindow;

            appleMusicWindow = FindAppleMusicWindow();
            try {
                if (appleMusicWindow != null) {
                    appleMusicInfo = GetAppleMusicInfo(appleMusicWindow);
                }
            } catch (Exception e) {
                Trace.WriteLine($"Something went wrong while scraping: {e}");
            }
            refreshHandler(appleMusicInfo);
        }
        public static AutomationElement? FindAppleMusicWindow() {
            try {
                var allWindows = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
                foreach (AutomationElement element in allWindows) {
                    var elementProperties = element.Current;
                    // TODO - How do we tell it's the actual Windows-native Apple Music application and not some other one?
                    if (elementProperties.Name == "Apple Music" && elementProperties.ClassName == "WinUIDesktopWin32WindowClass") {
                        return element;
                    }
                }
            } catch (ElementNotAvailableException) {
                return null;
            }
            return null;
        }

        public AppleMusicInfo? GetAppleMusicInfo(AutomationElement amWindow) {

            // ================================================
            //  Check if there is a song playing
            // ------------------------------------------------

            var amWinChild = amWindow.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.NameProperty, "DesktopChildSiteBridge"));
            var songFields = amWinChild.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.AutomationIdProperty, "myScrollViewer"));

            if (songFields.Count != 2) {
                return null;
            }

            // ================================================
            //  Get song info
            // ------------------------------------------------
            
            var songNameElement = songFields[0];
            var songAlbumArtistElement = songFields[1];

            // the upper rectangle is the song name; the bottom rectangle is the author/album
            // lower .Bottom = higher up on the screen (?)
            if (songNameElement.Current.BoundingRectangle.Bottom > songAlbumArtistElement.Current.BoundingRectangle.Bottom) {
                songNameElement = songFields[1];
                songAlbumArtistElement = songFields[0];
            }

            var songName = songNameElement.Current.Name;
            var songAlbumArtist = songAlbumArtistElement.Current.Name;

            string songArtist; 
            string songAlbum;
            try {
                // this is the U+2014 emdash, not the standard "-" character on the keyboard!
                songArtist = songAlbumArtist.Split(" — ")[0];
                songAlbum = songAlbumArtist.Split(" — ")[1];
            } catch {
                Trace.WriteLine($"Could not parse '{songAlbumArtist}' into artist and album.");
                songArtist = "";
                songAlbum = "";
            }

            // if this is a new song, clear out the current song
            if (currentSong == null || currentSong?.SongName != songName || currentSong?.SongSubTitle != songAlbumArtist) {
                currentSong = new AppleMusicInfo(songName, songAlbumArtist, songAlbum, songArtist);
            }

            // ================================================
            //  Get song timestamps
            // ------------------------------------------------

            var currentTimeElement = amWinChild.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.AutomationIdProperty, "CurrentTime"));
            var remainingDurationElement = amWinChild.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.AutomationIdProperty, "Duration"));

            // grab the seek slider to check song playback progress
            var songProgressSlider = amWinChild
                .FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.AutomationIdProperty, "LCDScrubber"))? // this may be hidden when a song is initialising
                .GetCurrentPattern(RangeValuePattern.Pattern) as RangeValuePattern;
            var songProgressPercent = songProgressSlider == null ? 0 : songProgressSlider.Current.Value / songProgressSlider.Current.Maximum;

            // calculate song timestamps
            int currentTime;
            int remainingDuration;

            // if the timestamps are being hidden by Apple Music, we fall back to independent timestamp calculation
            if (currentTimeElement == null || remainingDurationElement == null) {
                if (currentSong.SongDuration == null) {
                    string? dur = AppleMusicWebScraper.GetSongDuration(songName, songAlbum, songArtist);
                    currentSong.SongDuration = dur == null ? 0 : ParseTimeString(dur);
                }
                var songDuration = currentSong.SongDuration;
                currentTime = (int)(songProgressPercent * songDuration);
                remainingDuration = (int)((1 - songProgressPercent) * songDuration);
            } else { // ... otherwise just use the timestamps provided by Apple Music
                currentTime = ParseTimeString(currentTimeElement!.Current.Name);
                remainingDuration = ParseTimeString(remainingDurationElement!.Current.Name);
            }

            // convert into Unix timestamps for Discord
            currentSong.PlaybackStart = DateTime.UtcNow - new TimeSpan(0, 0, currentTime);
            currentSong.PlaybackEnd = DateTime.UtcNow + new TimeSpan(0, 0, remainingDuration);

            // check if the song is paused or not
            var playPauseButton = amWinChild.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.AutomationIdProperty, "TransportControl_PlayPauseStop"));
            currentSong.IsPaused = playPauseButton.Current.Name == "Play";


            // ================================================
            //  Get song cover art
            // ------------------------------------------------
            if (currentSong.CoverArtUrl == null) {
                currentSong.CoverArtUrl = AppleMusicWebScraper.GetAlbumArtUrl(songName, songAlbum, songArtist);
            }

            return currentSong;
        }

        // e.g. parse "-1:30" to 90 seconds
        private static int ParseTimeString(string time) {

            // remove leading "-"
            if (time.Contains('-')) {
                time = time.Split('-')[1];
            }

            int min = int.Parse(time.Split(":")[0]);
            int sec = int.Parse(time.Split(":")[1]);

            return min * 60 + sec;
        }
    }
}
