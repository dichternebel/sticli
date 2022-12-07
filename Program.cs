using System.Configuration;
using System.Diagnostics;
using Newtonsoft.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Auth;
using Swan.Logging;
using static SpotifyAPI.Web.Scopes;
using static SpotifyTrackInfo.Native;

namespace SpotifyTrackInfo
{
    public class Program
    {
        private static readonly string? clientId = ConfigurationManager.AppSettings["ClientID"];
        private static readonly HttpClient _httpClient = new();

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
        private static SpotifyTimer spotifyPollTimer;
        private static ConsoleEventDelegate handler;
        private static EmbedIOAuthServer _server;
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.

        private static void Exiting() => Console.CursorVisible = true;

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
        private static string OutputFolder { get; set; }
        private static string CredentialsPath { get; set; }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.

        private static string? currenTrackId { get; set; }

        private static bool isActive { get; set; }

        private static bool isPlaying { get; set; }

        public static async Task<int> Main()
        {
            // This is a bug in the SWAN Logging library, need this hack to bring back the cursor
            AppDomain.CurrentDomain.ProcessExit += (sender, e) => Exiting();

            // Name this thing
            Console.Title = $"STICLI - SpotifyTrackInfoCLI";

            try
            {
                SetQuickEditMode(false);

                handler = new ConsoleEventDelegate(ConsoleEventCallback);
                SetConsoleCtrlHandler(handler, true);

                // initialize paths
                var baseDir = System.AppContext.BaseDirectory;
                CredentialsPath = Path.Combine(baseDir, "credentials.json");
                OutputFolder = Path.Combine(baseDir, "output");

                if (!Directory.Exists(OutputFolder))
                {
                    var di = Directory.CreateDirectory(OutputFolder);

                    if (di.Exists)
                    {
                        "Output folder created successfully.".Info();
                    }
                }

                if (!File.Exists(CredentialsPath))
                {
                    _server = new(new Uri("http://localhost:5000/spotifyCallback"), 5000);
                    await StartAuthentication();
                }
                else
                {
                    await StartAsync();
                }

                // Block this task until the program is closed.
                await Task.Delay(-1);
                return 0;
            }
            catch (Exception ex)
            {
                $"[Main]: {ex.Message}".Error();
                return 1;
            }
        }

        private static bool ConsoleEventCallback(int eventType)
        {
            if (eventType == 2)
            {
                spotifyPollTimer.Elapsed -= SpotifyPollTimer_Elapsed;
                spotifyPollTimer.Stop();
                UpdateTrackInfo(new FullTrack()).Wait();
            }
            return false;
        }

        private static async Task StartAsync()
        {
            var json = await File.ReadAllTextAsync(CredentialsPath);
            var token = JsonConvert.DeserializeObject<PKCETokenResponse>(json);

            var authenticator = new PKCEAuthenticator(clientId!, token!);
            authenticator.TokenRefreshed += (sender, token) => File.WriteAllText(CredentialsPath, JsonConvert.SerializeObject(token));

            var config = SpotifyClientConfig.CreateDefault().WithAuthenticator(authenticator);
            var spotify = new SpotifyClient(config);

            // Get the current user profile
            var userProfile = await spotify.UserProfile.Current();
            GreetUser(userProfile);

            // Rename this thing
            Console.Title = $"STICLI - SpotifyTrackInfoCLI listening to {userProfile.DisplayName}";

            // initialize
            isActive = true;
            spotifyPollTimer = new SpotifyTimer(1000, spotify);
            spotifyPollTimer.Elapsed += SpotifyPollTimer_Elapsed;
            spotifyPollTimer.Start();
        }

        private static void SpotifyPollTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type.
#pragma warning disable CS8602 // Dereference of a possibly null reference.
            var spotify = ((SpotifyTimer)sender).Spotify;
#pragma warning restore CS8602 // Dereference of a possibly null reference.
#pragma warning restore CS8600 // Converting null literal or possible null value to non-nullable type.

            // Check if we have an active player or not
            var currentPlayback = spotify.Player.GetCurrentPlayback().Result;

            if (currentPlayback != null && currentPlayback.Device.IsActive)
            {
                isActive = true;
                var result = GetCurrentTrackInfo(spotify).Result;
                if (!string.IsNullOrEmpty(result))
                {
                    result.Info();
                }
            }
            else if (isActive)
            {
                isActive= false;
                UpdateTrackInfo(new FullTrack()).Wait();
                "No active device found... But I keep on searching.".Warn();
            }
        }

        private static async Task StartAuthentication()
        {
            var (verifier, challenge) = PKCEUtil.GenerateCodes();

            await _server.Start();
            _server.AuthorizationCodeReceived += async (sender, response) =>
            {
                await _server.Stop();
                PKCETokenResponse token = await new OAuthClient().RequestToken(
                  new PKCETokenRequest(clientId!, response.Code, _server.BaseUri, verifier)
                );

                await File.WriteAllTextAsync(CredentialsPath, JsonConvert.SerializeObject(token));
                _server.Dispose();
                "That worked! Now let's start this thing...".Info();
                await StartAsync();
            };

            var request = new LoginRequest(_server.BaseUri, clientId!, LoginRequest.ResponseType.Code)
            {
                CodeChallenge = challenge,
                CodeChallengeMethod = "S256",
                Scope = new List<string> { UserReadPlaybackState, UserReadCurrentlyPlaying, UserReadPlaybackPosition }
            };

            Uri uri = request.ToUri();
            try
            {
                BrowserUtil.Open(uri);
            }
            catch (Exception)
            {
                $"Unable to open URL, manually open: {uri}".Error();
            }
        }

        private static void GreetUser(PrivateUser userProfile)
        {
            $"Moin {userProfile.DisplayName}!".Info();
            $"Your Spotify account with ID {userProfile.Id} is authenticated.".Info();
        }

        private static async Task<string> GetCurrentTrackInfo(SpotifyClient spotify)
        {
            var result = "";
            var currentlyPlayingRequest = new PlayerCurrentlyPlayingRequest(PlayerCurrentlyPlayingRequest.AdditionalTypes.Track);
            var currentlyPlaying = await spotify.Player.GetCurrentlyPlaying(currentlyPlayingRequest);
            if (currentlyPlaying != null && currentlyPlaying.IsPlaying)
            {
                isPlaying = true;
                var fullTrack = ((FullTrack)currentlyPlaying.Item);

                if (currentlyPlaying.ProgressMs.HasValue)
                {
                    var progressReadable = ConvertIntoReadableTime(currentlyPlaying.ProgressMs.Value);

                    await File.WriteAllTextAsync(Path.Combine(OutputFolder, "progressMs.txt"), $"{currentlyPlaying.ProgressMs.Value}");
                    await File.WriteAllTextAsync(Path.Combine(OutputFolder, "progress.txt"), $"{progressReadable}");

                    if (fullTrack != null)
                    {
                        var remainingMs = fullTrack.DurationMs - currentlyPlaying.ProgressMs.Value;

                        await File.WriteAllTextAsync(Path.Combine(OutputFolder, "progress_duration.txt"), $"{progressReadable}/{ConvertIntoReadableTime(fullTrack.DurationMs)}");
                        await File.WriteAllTextAsync(Path.Combine(OutputFolder, "remainingMs.txt"), $"{remainingMs}");
                        await File.WriteAllTextAsync(Path.Combine(OutputFolder, "remaining.txt"), $"{ConvertIntoReadableTime(remainingMs)}");
                    }
                }
                
                
                if (fullTrack != null)
                {
                    if (currenTrackId != fullTrack.Id)
                    {
                        await UpdateTrackInfo(fullTrack);
                        result = $"Currently playing: '{fullTrack.Artists[0].Name} - {fullTrack.Name}' -> {fullTrack.ExternalUrls["spotify"]}";
                    }
                }
                
            }
            else if (isPlaying)
            {
                isPlaying = false;
                await UpdateTrackInfo(new FullTrack());
                result = "No playing track found... But I keep on listening.";
            }

            return result;
        }

        private static async Task UpdateTrackInfo(FullTrack fullTrack)
        {
            currenTrackId = fullTrack.Id;

            if (currenTrackId != null)
            {
                // Prepare artists
                var artistNames = new List<string>();
                foreach (var item in fullTrack.Artists)
                {
                    artistNames.Add(item.Name);
                }
                var artists = string.Join(',', artistNames.ToArray());

                // Write out all info
                await File.WriteAllTextAsync(Path.Combine(OutputFolder, "artist.txt"), $"{fullTrack.Artists[0].Name}");
                await File.WriteAllTextAsync(Path.Combine(OutputFolder, "artists.txt"), $"{artists}");
                await File.WriteAllTextAsync(Path.Combine(OutputFolder, "track.txt"), $"{fullTrack.Name}");
                await File.WriteAllTextAsync(Path.Combine(OutputFolder, "trackId.txt"), $"{currenTrackId}");
                await File.WriteAllTextAsync(Path.Combine(OutputFolder, "artist-track.txt"), $"{fullTrack.Artists[0].Name} - {fullTrack.Name}");
                await File.WriteAllTextAsync(Path.Combine(OutputFolder, "artists-track.txt"), $"{artists} - {fullTrack.Name}");
                await File.WriteAllTextAsync(Path.Combine(OutputFolder, "album.txt"), $"{fullTrack.Album.Name}");
                await File.WriteAllTextAsync(Path.Combine(OutputFolder, "uri.txt"), $"{fullTrack.Uri}");
                await File.WriteAllTextAsync(Path.Combine(OutputFolder, "url.txt"), $"{fullTrack.ExternalUrls["spotify"]}");
                await File.WriteAllTextAsync(Path.Combine(OutputFolder, "duration.txt"), $"{ConvertIntoReadableTime(fullTrack.DurationMs)}");
                await File.WriteAllTextAsync(Path.Combine(OutputFolder, "durationMs.txt"), $"{fullTrack.DurationMs}");

                for (int i = 0; i < fullTrack.Album.Images.Count; i++)
                {
                    var identifier = "";
                    switch (i)
                    {
                        case 0:
                            identifier = "640x640";
                            break;
                        case 1:
                            identifier = "300x300";
                            break;
                        case 2:
                            identifier = "64x64";
                            break;
                        default:
                            break;
                    }
                    if (string.IsNullOrEmpty(identifier)) break;

                    var byteArray = await _httpClient.GetByteArrayAsync(fullTrack.Album.Images[i].Url);
                    var image = SixLabors.ImageSharp.Image.Load(byteArray);
                    await image.SaveAsPngAsync(Path.Combine(OutputFolder, $"CoverArt_{identifier}.png"));
                }
            }
            else // Clean up the mess
            {
                await File.WriteAllTextAsync(Path.Combine(OutputFolder, "artist.txt"), "");
                await File.WriteAllTextAsync(Path.Combine(OutputFolder, "artists.txt"), "");
                await File.WriteAllTextAsync(Path.Combine(OutputFolder, "track.txt"), "");
                await File.WriteAllTextAsync(Path.Combine(OutputFolder, "trackId.txt"), "");
                await File.WriteAllTextAsync(Path.Combine(OutputFolder, "artist-track.txt"), "");
                await File.WriteAllTextAsync(Path.Combine(OutputFolder, "artists-track.txt"), "");
                await File.WriteAllTextAsync(Path.Combine(OutputFolder, "album.txt"), "");
                await File.WriteAllTextAsync(Path.Combine(OutputFolder, "album.txt"), "");
                await File.WriteAllTextAsync(Path.Combine(OutputFolder, "uri.txt"), "");
                await File.WriteAllTextAsync(Path.Combine(OutputFolder, "url.txt"), "");
                await File.WriteAllTextAsync(Path.Combine(OutputFolder, "durationMs.txt"), "");
                await File.WriteAllTextAsync(Path.Combine(OutputFolder, "duration.txt"), "");
                await File.WriteAllTextAsync(Path.Combine(OutputFolder, "progress.txt"), "");
                await File.WriteAllTextAsync(Path.Combine(OutputFolder, "progressMs.txt"), "");
                await File.WriteAllTextAsync(Path.Combine(OutputFolder, "progress_duration.txt"), "");
                await File.WriteAllTextAsync(Path.Combine(OutputFolder, "remainingMs.txt"), "");
                await File.WriteAllTextAsync(Path.Combine(OutputFolder, "remaining.txt"), "");

                using (Image<Rgba32> image = new(640, 640))
                {
                    await image.SaveAsPngAsync(Path.Combine(OutputFolder, "CoverArt_640x640.png"));
                }
                using (Image<Rgba32> image = new(300, 300))
                {
                    await image.SaveAsPngAsync(Path.Combine(OutputFolder, "CoverArt_300x300.png"));
                }
                using (Image<Rgba32> image = new(64, 64))
                {
                    await image.SaveAsPngAsync(Path.Combine(OutputFolder, "CoverArt_64x64.png"));
                }
            }

            await File.WriteAllTextAsync(Path.Combine(OutputFolder, "isActive.txt"), $"{isActive}");
            await File.WriteAllTextAsync(Path.Combine(OutputFolder, "isPlaying.txt"), $"{isPlaying}");
        }

        private static string ConvertIntoReadableTime(int milliseconds)
        {
            var timeSpan = TimeSpan.FromMilliseconds(milliseconds);
            int h = timeSpan.Hours;
            int m = timeSpan.Minutes;
            int s = timeSpan.Seconds;

            if (h > 0) return $"{h}:{m:D2}:{s:D2}";
            return $"{m}:{s:D2}";
        }
    }
}