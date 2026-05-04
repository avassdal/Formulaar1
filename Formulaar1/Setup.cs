using System.Text.Json;
using System.Text.Json.Nodes;

namespace Formulaar1
{
    internal static class Setup
    {
        private const string SettingsFile = "appsettings.json";
        private const string DefaultHardlinkPath = "/full/path/to/hardlink/folder";

        internal static bool IsRequired(IConfiguration config)
        {
            var sonarKey = config.GetValue<string>("APICredentials:Sonarr:ApiKey");
            var sonarPath = config.GetValue<string>("APICredentials:Sonarr:BasePath");
            var hardlink = config.GetValue<string>("Hardlinkpath");

            return string.IsNullOrWhiteSpace(sonarKey)
                || string.IsNullOrWhiteSpace(sonarPath)
                || string.IsNullOrWhiteSpace(hardlink)
                || hardlink == DefaultHardlinkPath;
        }

        internal static void RunWizard()
        {
            Console.WriteLine();
            Console.WriteLine("╔══════════════════════════════════════════════════╗");
            Console.WriteLine("║          Formulaar1 — First-Run Setup            ║");
            Console.WriteLine("╚══════════════════════════════════════════════════╝");
            Console.WriteLine("No valid configuration found. Let's set things up.");
            Console.WriteLine();

            var sonarPath = Prompt("Sonarr base URL", "http://127.0.0.1:8989");
            var sonarKey = PromptRequired("Sonarr API key (Settings → General)");
            var hardlinkPath = PromptRequired("Hardlink path (full path to folder where Formulaar1 creates links)");

            Console.WriteLine();
            Console.WriteLine("── qBittorrent ──────────────────────────────────");
            var qbitPath = Prompt("qBittorrent Web UI URL", "http://127.0.0.1:10169");
            var qbitUser = Prompt("qBittorrent username", "admin");
            var qbitPass = PromptPassword("qBittorrent password");

            Console.WriteLine();
            Console.WriteLine("── Bugsnag (optional — press Enter to skip) ─────");
            var bugsnagKey = Prompt("Bugsnag API key", "");
            var bugsnagEnabled = !string.IsNullOrWhiteSpace(bugsnagKey);

            var settings = new JsonObject
            {
                ["Logging"] = new JsonObject
                {
                    ["LogLevel"] = new JsonObject
                    {
                        ["Default"] = "Information",
                        ["Microsoft.AspNetCore"] = "Warning",
                    },
                },
                ["AllowedHosts"] = "*",
                ["TorrentClient"] = "qBittorrent",
                ["Hardlinkpath"] = hardlinkPath,
                ["APICredentials"] = new JsonObject
                {
                    ["Sonarr"] = new JsonObject
                    {
                        ["ApiKey"] = sonarKey,
                        ["BasePath"] = sonarPath,
                    },
                    ["qBittorrentClient"] = new JsonObject
                    {
                        ["Username"] = qbitUser,
                        ["Password"] = qbitPass,
                        ["BasePath"] = qbitPath,
                    },
                    ["bugsnag"] = new JsonObject
                    {
                        ["apiKey"] = bugsnagKey,
                        ["enabled"] = bugsnagEnabled,
                    },
                },
            };

            var json = settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFile, json);

            Console.WriteLine();
            Console.WriteLine($"✓ Configuration saved to {SettingsFile}");
            Console.WriteLine();
        }

        private static string Prompt(string label, string defaultValue)
        {
            if (!string.IsNullOrEmpty(defaultValue))
                Console.Write($"  {label} [{defaultValue}]: ");
            else
                Console.Write($"  {label}: ");

            var input = Console.ReadLine()?.Trim();
            return string.IsNullOrEmpty(input) ? defaultValue : input;
        }

        private static string PromptRequired(string label)
        {
            while (true)
            {
                Console.Write($"  {label}: ");
                var input = Console.ReadLine()?.Trim();
                if (!string.IsNullOrEmpty(input))
                    return input;
                Console.WriteLine("  → This field is required.");
            }
        }

        private static string PromptPassword(string label)
        {
            Console.Write($"  {label}: ");
            var password = new System.Text.StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    break;
                }
                if (key.Key == ConsoleKey.Backspace && password.Length > 0)
                {
                    password.Remove(password.Length - 1, 1);
                    Console.Write("\b \b");
                }
                else if (key.Key != ConsoleKey.Backspace)
                {
                    password.Append(key.KeyChar);
                    Console.Write('*');
                }
            }
            return password.ToString();
        }
    }
}
