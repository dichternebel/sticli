using SpotifyAPI.Web;

namespace SpotifyTrackInfo
{
    public class SpotifyTimer : System.Timers.Timer
    {
        public SpotifyClient Spotify { get; private set; }

        public SpotifyTimer(int interaval, SpotifyClient spotifyClient)
        {
            base.Interval = interaval;
            this.Spotify = spotifyClient;
        }
    }
}
