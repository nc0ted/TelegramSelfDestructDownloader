using System.Text.Json;
using TL;
using WTelegram;

namespace TelegramSelfDestructDownloader
{
    class ConfigModel
    {
        public bool EnableWTelegramLogs { get; set; }
        public int ApiId { get; set; }
        public string ApiHash { get; set; }
        public string PhoneNumber { get; set; }
        public bool ForwardToSavedMessages { get; set; } = true;
        public int MaxFileSizeMB { get; set; } = 250;
        public bool IncludeChatTitleInCaption { get; set; } = true;
        public bool AutoCreateConfig { get; set; } = true;
    }

    class Program
    {
        private static readonly Dictionary<long, User> Users = new();
        private static readonly Dictionary<long, ChatBase> Chats = new();
        private const string ConfigFileName = "appsettings.json";
        private const string LogFolder = "Logs";

        static async Task Main(string[] args)
        {
            Directory.CreateDirectory(LogFolder);
            Info("Starting TelegramSelfDestructDownloader...");

            var cfg = LoadOrCreateConfig();
            if (cfg == null)
            {
                Console.WriteLine("Configuration was not created. Exiting.");
                return;
            }

            try
            {
                if (cfg.EnableWTelegramLogs)
                {
                    WTelegram.Helpers.Log = (level, message) =>
                    {
                        try
                        {
                            File.AppendAllText(
                                Path.Combine(LogFolder, "wtelegram.log"),
                                $"[{DateTime.UtcNow:O}] [{level}] {message}{Environment.NewLine}"
                            );
                        }
                        catch { }
                    };
                }
                else
                {
                    WTelegram.Helpers.Log = (lvl, str) => { };
                }

                using var client = new Client(ConfigFromFile(cfg));

                var me = await client.LoginUserIfNeeded();
                Console.WriteLine($"Logged in as: {me.username ?? $"{me.first_name} {me.last_name}".Trim()}");
                Log($"Logged in as: {me.id} / {me.username}");

                client.OnUpdates += async updates =>
                {
                    if (updates is UpdatesBase baseUpdates)
                    {
                        try
                        {
                            baseUpdates.CollectUsersChats(Users, Chats);
                            foreach (var upd in baseUpdates.UpdateList)
                            {
                                Message message = null;
                                if (upd is UpdateNewMessage unm) message = unm.message as Message;
                                else if (upd is UpdateNewChannelMessage uncm) message = uncm.message as Message;

                                if (message != null) await HandleMessage(client, message, cfg);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"Error in OnUpdates handler: {ex}");
                        }
                    }
                };

                Console.WriteLine("Listening for incoming messages. Press Ctrl+C to exit.");
                await Task.Delay(-1);
            }
            catch (Exception ex)
            {
                Log($"Fatal error in Main: {ex}");
                Console.WriteLine("Fatal error. See Logs/log.txt for details.");
            }
        }
        
        static Func<string, string?> ConfigFromFile(ConfigModel cfg)
        {
            var sessionPath = GetSessionPath(cfg);

            return what => what switch
            {
                "api_id" => cfg.ApiId.ToString(),
                "api_hash" => cfg.ApiHash,
                "phone_number" => cfg.PhoneNumber,
                "session_pathname" => sessionPath,
                "verification_code" => Prompt("Enter the verification code from Telegram:"),
                "password" => Prompt("Enter your 2FA password (leave empty if none):", hideInput: true),
                _ => null
            };
        }

        static string GetSessionPath(ConfigModel cfg)
        {
            var safePhone = (cfg.PhoneNumber ?? "unknown")
                .Replace("+", "")
                .Replace(" ", "")
                .Replace("-", "");

            Directory.CreateDirectory("sessions");
            return Path.Combine("sessions", $"session_{safePhone}.dat");
        }

        static string Prompt(string text, bool hideInput = false)
        {
            Console.Write(text + " ");
            if (!hideInput) return Console.ReadLine() ?? string.Empty;
            var pwd = string.Empty;
            ConsoleKeyInfo key;
            while ((key = Console.ReadKey(true)).Key != ConsoleKey.Enter)
            {
                if (key.Key == ConsoleKey.Backspace && pwd.Length > 0)
                {
                    pwd = pwd[..^1];
                    Console.Write("\b \b");
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    pwd += key.KeyChar;
                    Console.Write('*');
                }
            }
            Console.WriteLine();
            return pwd;
        }

        static ConfigModel? LoadOrCreateConfig()
        {
            if (File.Exists(ConfigFileName))
            {
                try
                {
                    var json = File.ReadAllText(ConfigFileName);
                    var cfg = JsonSerializer.Deserialize<ConfigModel>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (cfg != null && cfg.ApiId != 0 && !string.IsNullOrWhiteSpace(cfg.ApiHash) && !string.IsNullOrWhiteSpace(cfg.PhoneNumber))
                        return cfg;

                    Console.WriteLine("Config file is incomplete. You will be guided to create it.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Failed to read config file: " + ex.Message);
                }
            }

            Console.WriteLine("Create a new configuration (appsettings.json). This is needed only once.");
            if (!PromptYesNo("Create config now? (Y/n)")) return null;

            var newCfg = new ConfigModel();
            
            while (true)
            {
                Console.Write("ApiId (digits): ");
                var aid = Console.ReadLine();
                if (int.TryParse(aid, out var parsed) && parsed > 0) { newCfg.ApiId = parsed; break; }
                Console.WriteLine("Please enter a valid numeric ApiId.");
            }
            Console.Write("ApiHash: "); newCfg.ApiHash = Console.ReadLine() ?? string.Empty;
            Console.Write("Phone number (with +): "); newCfg.PhoneNumber = Console.ReadLine() ?? string.Empty;

            Console.WriteLine("Saving config to " + ConfigFileName);
            var content = JsonSerializer.Serialize(newCfg, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigFileName, content);
            Log("Configuration file created.");
            return newCfg;
        }

        static bool PromptYesNo(string question)
        {
            Console.Write(question + " ");
            var ans = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(ans)) return true;
            ans = ans.Trim().ToLowerInvariant();
            return ans == "y" || ans == "yes";
        }

        static async Task HandleMessage(Client client, Message message, ConfigModel cfg)
        {
            if (message == null) return;
            if ((message.flags & Message.Flags.out_) != 0) return;

            object? media = null;
            string? fileName = null;
            int? ttl = null;

            string chatTitle = "Unknown chat";
            if (message.Peer is PeerUser peerUser && Users.TryGetValue(peerUser.user_id, out var user))
                chatTitle = user.username ?? (user.first_name + " " + user.last_name).Trim();
            else if (message.Peer is PeerChat peerChat && Chats.TryGetValue(peerChat.chat_id, out var chat))
                chatTitle = chat.Title ?? "Group";
            else if (message.Peer is PeerChannel peerChannel && Chats.TryGetValue(peerChannel.channel_id, out var channel))
                chatTitle = channel.Title ?? "Channel";

            if (message.media is MessageMediaPhoto photoMedia && photoMedia.ttl_seconds > 0)
            {
                media = photoMedia.photo as Photo;
                ttl = photoMedia.ttl_seconds;
                fileName = $"viewonce_photo_{message.id}.jpg";
            }
            else if (message.media is MessageMediaDocument docMedia && docMedia.ttl_seconds > 0)
            {
                media = docMedia.document as Document;
                ttl = docMedia.ttl_seconds;
                fileName = (docMedia.document as Document)?.Filename ?? $"viewonce_media_{message.id}";
            }

            if (media == null || ttl == null) return;

            var safeName = MakeSafeFileName(fileName ?? $"media_{message.id}");

            Log($"Detected view-once media ({ttl}s) from {chatTitle}: {safeName}");
            Console.WriteLine($"Detected view-once media from {chatTitle}");

            var tempFilePath = Path.GetTempFileName();
            try
            {
                await using (var fs = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write))
                {
                    if (media is Photo photo)
                        await client.DownloadFileAsync(photo, fs);
                    else if (media is Document document)
                        await client.DownloadFileAsync(document, fs);
                }

                var fileInfo = new FileInfo(tempFilePath);
                if (fileInfo.Length > cfg.MaxFileSizeMB * 1024L * 1024L)
                {
                    Log($"Skipping file {safeName} from {chatTitle}: size {fileInfo.Length} bytes exceeds limit {cfg.MaxFileSizeMB} MB");
                    Console.WriteLine($"Skipping large file from {chatTitle}");
                    return;
                }

                await using (var fs = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read))
                {
                    var uploadedFile = await client.UploadFileAsync(fs, safeName);
                    
                    if (cfg.ForwardToSavedMessages)
                    {
                        var caption = cfg.IncludeChatTitleInCaption ? $"From: {chatTitle}" : "";
                        await client.SendMediaAsync(new InputPeerSelf(), caption, uploadedFile);
                        Log($"Forwarded {safeName} to Saved Messages (from {chatTitle})");
                        Console.WriteLine($"Forwarded view-once media {safeName} from {chatTitle}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Error processing message {message.id}: {ex}");
                Console.WriteLine($"Error processing message from {chatTitle}: {ex.Message}");
            }
            finally
            {
                try { File.Delete(tempFilePath); } catch { }
            }
        }

        static string MakeSafeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

      
        static void Log(string text)
        {
            try
            {
                var line = $"[{DateTime.UtcNow:O}] {text}{Environment.NewLine}";
                File.AppendAllText(Path.Combine(LogFolder, "log.txt"), line);
            }
            catch { }
        }

        static void Info(string text)
        {
            Console.WriteLine(text);
        }
    }
}