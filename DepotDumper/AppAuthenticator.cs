#nullable enable
using System;
using System.Threading.Tasks;
using SteamKit2.Authentication;

namespace DepotDumper
{
    public enum AuthPromptKind
    {
        /// <summary>Code from the Steam Mobile App authenticator.</summary>
        DeviceCode,
        /// <summary>Code Steam emailed to the account.</summary>
        EmailCode,
    }

    /// <summary>
    /// Steam Guard authenticator that asks for codes through <see cref="Steam3Session.AuthCodePrompt"/>
    /// (the GUI dialog) when one is registered, and falls back to SteamKit's console prompts otherwise.
    /// SteamKit's stock UserConsoleAuthenticator reads from Console.ReadLine, which a windowed app doesn't have.
    /// </summary>
    internal sealed class AppAuthenticator : IAuthenticator
    {
        private readonly IAuthenticator consoleFallback = new UserConsoleAuthenticator();

        public async Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
        {
            var prompt = Steam3Session.AuthCodePrompt;
            if (prompt == null)
                return await consoleFallback.GetDeviceCodeAsync(previousCodeWasIncorrect);

            Logger.Info(previousCodeWasIncorrect
                ? "Steam Guard code was incorrect, asking again..."
                : "Steam Guard: waiting for authenticator app code...");
            return await RequireCode(prompt, AuthPromptKind.DeviceCode, null, previousCodeWasIncorrect);
        }

        public async Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
        {
            var prompt = Steam3Session.AuthCodePrompt;
            if (prompt == null)
                return await consoleFallback.GetEmailCodeAsync(email, previousCodeWasIncorrect);

            Logger.Info(previousCodeWasIncorrect
                ? "Steam Guard email code was incorrect, asking again..."
                : $"Steam Guard: waiting for the code emailed to {email}...");
            return await RequireCode(prompt, AuthPromptKind.EmailCode, email, previousCodeWasIncorrect);
        }

        public Task<bool> AcceptDeviceConfirmationAsync()
        {
            if (Steam3Session.AuthCodePrompt == null)
                return consoleFallback.AcceptDeviceConfirmationAsync();

            Logger.Info("Steam Guard: approve this sign-in in the Steam Mobile App to continue.");
            return Task.FromResult(true);
        }

        private static async Task<string> RequireCode(
            Func<AuthPromptKind, string?, bool, Task<string?>> prompt,
            AuthPromptKind kind, string? email, bool previousCodeWasIncorrect)
        {
            var code = await prompt(kind, email, previousCodeWasIncorrect);
            if (string.IsNullOrWhiteSpace(code))
                throw new OperationCanceledException("Steam Guard code entry was cancelled.");
            return code.Trim();
        }
    }
}
