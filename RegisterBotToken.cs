using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace botStarsSaller
{
    internal class RegisterBotToken
    {
        private const string TokenFile = "telegram_token.txt";
        public string BotToken { get; }

        public RegisterBotToken()
        {
            BotToken = GetOrRequestToken();
        }

        private static string GetOrRequestToken()
        {
            if (File.Exists(TokenFile))
            {
                var token = File.ReadAllText(TokenFile).Trim();
                if (!string.IsNullOrWhiteSpace(token))
                    return token;
            }

            Console.Write("Введите токен Telegram-бота: ");
            var input = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(input))
                Console.WriteLine("Токен бота не может быть пустым!");

            File.WriteAllText(TokenFile, input);
            return input;
        }
    }

    internal class BotAccessVerifier
    {
        private const string AccountFile = "accountTelegram.txt";
        private readonly ITelegramBotClient _bot;
        private readonly int _secretCode;
        private readonly TaskCompletionSource<long> _tcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);

        public BotAccessVerifier(string botToken, int secretCode)
        {
            _bot = new TelegramBotClient(botToken);
            _secretCode = secretCode;
        }

        public static bool TryReadAuthorizedUserId(out long userId)
        {
            userId = 0;
            if (!File.Exists(AccountFile)) return false;
            var raw = File.ReadAllText(AccountFile).Trim();
            return long.TryParse(raw, out userId) && userId > 0;
        }

        public async Task<long> WaitForVerificationAsync(CancellationToken ct)
        {
            var receiverOptions = new ReceiverOptions
            {
                AllowedUpdates = new[] { UpdateType.Message }
            };

            using var ctsLinked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _bot.StartReceiving(HandleUpdateAsync, HandleErrorAsync, receiverOptions, ctsLinked.Token);

            var me = await _bot.GetMe(ct);

            var userId = await _tcs.Task; // ждём корректного ввода кода
            ctsLinked.Cancel();

            File.WriteAllText(AccountFile, userId.ToString());
            return userId;
        }

        private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
        {
            if (update.Type != UpdateType.Message) return;
            var msg = update.Message;
            if (msg == null || string.IsNullOrWhiteSpace(msg.Text)) return;

            // Сравниваем текст строго с кодом
            if (msg.Text.Trim() == _secretCode.ToString())
            {
                // Фиксируем user_id первого, кто прислал верный код
                if (!_tcs.Task.IsCompleted)
                {
                    _tcs.TrySetResult(msg.From?.Id ?? 0);
                }
                await bot.SendMessage(msg.Chat.Id, "Доступ подтверждён ✅", cancellationToken: ct);
            }
            else
            {
                // Ничего не делаем, можно ответить подсказкой при желании
            }
        }

        private Task HandleErrorAsync(ITelegramBotClient bot, Exception ex, CancellationToken ct)
        {
            Console.WriteLine($"[BOT ERROR] {ex.Message}");
            return Task.CompletedTask;
        }
    }
}
