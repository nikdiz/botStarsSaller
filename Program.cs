using Telegram.Bot;
using Telegram.Bot.Types.Enums;

using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using System.Net.WebSockets;


namespace botStarsSaller
{
    internal class Program
    {
        //Обьявляем класс для исполнения ордеров
        static NewOrderProcessor _orderProcessor;
        static RegisterGoldenKey registerGoldenKey;

        //Обьявляем Телеграм клиент
        static TelegramBotClient _tgBot;
        static CancellationTokenSource _botCts;
        static Task _botTask;

        //Для мониторинга заказов
        static bool _isMonitoringRunning;
        static CancellationTokenSource _monitorCts;
        static Task _monitorTask;
        static List<(WTelegram.Client Client, string Phone)> _activeClients;

        static async Task Main(string[] args)
        {
            //golden key
            registerGoldenKey = new RegisterGoldenKey();
            Console.WriteLine("Golden key загружен.");

            //Токен бота
            var registerBotToken = new RegisterBotToken();
            Console.WriteLine("Токен бота загружен.");

            // 3) Проверка доступа через код, если user_id ещё не сохранён
            if (!BotAccessVerifier.TryReadAuthorizedUserId(out var userId))
            {
                var random = new Random();
                var code = random.Next(10000, 99999);
                Console.WriteLine($"КОД ДЛЯ ТГ-БОТА: {code}");
                Console.WriteLine("Отправьте этот код вашему боту в Telegram, чтобы продолжить...");

                var verifier = new BotAccessVerifier(registerBotToken.BotToken, code);
                try
                {
                    userId = verifier.WaitForVerificationAsync(CancellationToken.None).GetAwaiter().GetResult();
                    Console.WriteLine($"Доступ выдан. Ваш user_id: {userId}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка верификации: {ex.Message}");
                    return;
                }
            }
            else
            {
                Console.WriteLine($"Обнаружен ранее авторизованный user_id: {userId}. Пропускаем верификацию.");
            }

            StartBot(registerBotToken.BotToken);
            Console.WriteLine("Телеграм-бот запущен.");


            while (!_botCts.Token.IsCancellationRequested)
            {
                await Task.Delay(1000); // удерживает процесс живым
            }


            _botCts.Cancel();
        }

        static void StartBot(string token)
        {
            if (_botTask != null) return; // уже запущен

            _botCts = new CancellationTokenSource();
            _tgBot = new TelegramBotClient(token, cancellationToken: _botCts.Token);

            // подписки в твоём стиле
            _tgBot.OnMessage += async (Message msg, UpdateType type) =>
            {
                if (msg.Text is null) return;

                if (msg.Text == "/start")
                {
                    await SendMainMenu(msg.Chat.Id);
                }
            };

            _tgBot.OnUpdate += async (Update update) =>
            {
                await OnUpdate(update);
            };

            // фон: держим «жизненный цикл» до отмены
            _botTask = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, _botCts.Token);
                }
                catch (OperationCanceledException) { }
            });
        }


        private static async Task OnUpdate(Update update)
        {
            // 1) Обработка CallbackQuery (кнопок)
            if (update.CallbackQuery is { } query)
            {
                // подтверждаем, чтобы кнопка "не висела"
                try { await _tgBot.AnswerCallbackQuery(query.Id); } catch { }

                switch (query.Data)
                {
                    case "settings":
                        await SettingsBot(query);
                        break;

                    case "golden_key":
                        await ShowGoldenKey(query);
                        break;

                    case "change_golden_key":
                        await AskForGoldenKey(query);
                        break;

                    case "toggle_run":
                        if (!_isMonitoringRunning) await StartMonitoringFunpay(query);
                        else await StopMonitoringFunpay(query);
                        break;

                    case "clients":
                        // TODO
                        break;

                    case "userid":
                        // TODO
                        break;

                    case "back_to_main":
                        await SendMainMenu(query.Message.Chat.Id);
                        break;
                }
            }

            // 2) Обработка обычных сообщений (ввод пользователем нового ключа и т.п.)
            if (update.Message is { } msg && msg.Text is not null)
            {
                Console.WriteLine($"[DEBUG] Message from {msg.Chat.Id}: {msg.Text}");

                // если мы ждём Golden Key — сохраняем
                if (_isWaitingForGoldenKey && msg.Chat.Id == _waitingChatId)
                {
                    await SaveGoldenKey(msg.Chat.Id, msg.Text);
                    return;
                }

                // другие команды
                if (msg.Text == "/start")
                {
                    await SendMainMenu(msg.Chat.Id);
                }
            }
        }



        private static async Task ShowGoldenKey(CallbackQuery query)
        {
            string goldenKey = "Не найден";
            const string filePath = "goldenkey.txt";

            if (File.Exists(filePath))
            {
                goldenKey = await File.ReadAllTextAsync(filePath);
                if (string.IsNullOrWhiteSpace(goldenKey))
                    goldenKey = "Пусто";
            }

            var buttons = new List<InlineKeyboardButton[]>
    {
        new[] { InlineKeyboardButton.WithCallbackData("✏️ Изменить", "change_golden_key") },
        new[] { InlineKeyboardButton.WithCallbackData("⬅️ Назад", "settings") }
    };

            var keyboard = new InlineKeyboardMarkup(buttons);

            await _tgBot.EditMessageText(
                chatId: query.Message.Chat.Id,
                messageId: query.Message.MessageId,
                text: $"🔑 Текущий Golden Key:\n`{goldenKey}`",
                parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown,
                replyMarkup: keyboard
            );
        }

        private static bool _isWaitingForGoldenKey = false;
        private static long _waitingChatId;

        private static async Task AskForGoldenKey(CallbackQuery query)
        {
            _isWaitingForGoldenKey = true;
            _waitingChatId = query.Message.Chat.Id;

            await _tgBot.EditMessageText(
                chatId: query.Message.Chat.Id,
                messageId: query.Message.MessageId,
                text: "🔑 Введите новый Golden Key:\n(просто отправьте сообщение в этот чат)"
            );
        }

        private static async Task SaveGoldenKey(long chatId, string newKey)
        {
            try
            {
                const string filePath = "goldenkey.txt";
                await File.WriteAllTextAsync(filePath, newKey.Trim());

                _isWaitingForGoldenKey = false;
                _waitingChatId = 0;

                Console.WriteLine($"[DEBUG] Golden key saved: {newKey}");
                await _tgBot.SendMessage(chatId, "✅ Новый Golden Key сохранён!");
                await SendMainMenu(chatId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] SaveGoldenKey: {ex}");
                await _tgBot.SendMessage(chatId, "❌ Ошибка при сохранении ключа.");
            }
        }


        private static async Task SendMainMenu(long chatId)
        {
            await _tgBot.SendMessage(
                chatId: chatId,
                text: "FB_Stars",
                replyMarkup: GetMainMenu(chatId)
            );
        }

        private static InlineKeyboardMarkup GetMainMenu(long userId)
        {
            var stateLabel = _isMonitoringRunning ? "Мониторинг запущен ✅" : "Мониторинг отключен ⛔";

            var buttons = new List<InlineKeyboardButton[]>
    {
        new[] { InlineKeyboardButton.WithCallbackData("Настройки", "settings") },
        new[] { InlineKeyboardButton.WithCallbackData(stateLabel, "toggle_run") }
    };

            return new InlineKeyboardMarkup(buttons);
        }

        public static async Task SettingsBot(CallbackQuery query)
        {
            var buttons = new List<InlineKeyboardButton[]>
    {
        new[] { InlineKeyboardButton.WithCallbackData("Golden Key", "golden_key") },
        new[] { InlineKeyboardButton.WithCallbackData("Clients", "clients") },
        new[] { InlineKeyboardButton.WithCallbackData("UserId", "userid") },
        new[] { InlineKeyboardButton.WithCallbackData("⬅️ Назад", "back_to_main") }
    };

            var keyboard = new InlineKeyboardMarkup(buttons);

            await _tgBot.EditMessageText(
                chatId: query.Message.Chat.Id,
                messageId: query.Message.MessageId,
                text: "⚙️ Настройки",
                replyMarkup: keyboard
            );
        }


        public static async Task StartMonitoringFunpay(CallbackQuery query)
        {
            // Передаём клиентов
            var sessionsDir = Path.Combine(AppContext.BaseDirectory, "Sessions");
            Directory.CreateDirectory(sessionsDir);

            var clients = ClientsFactory.CreateClients(sessionsDir, AppContext.BaseDirectory);

            _activeClients = clients;

            if (clients.Count == 0)
            {
                await _tgBot.SendMessage(query.Message.Chat.Id, "Для начала добавьте аккаунты с которых будут передаваться звезды!");
                await _tgBot.AnswerCallbackQuery(query.Id);
                return;
            }

            _orderProcessor = new NewOrderProcessor(clients);

            // Запуск в фоне
            _monitorCts = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                try
                {
                    // Если можешь — пробрось токен в Run(token). Иначе оставь как есть.
                    _monitorTask = Task.Run(() => Run(_monitorCts.Token));
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                }
            }, _monitorCts.Token);

            _isMonitoringRunning = true;

            // Обновляем клавиатуру текущего сообщения
            await _tgBot.EditMessageReplyMarkup(
                chatId: query.Message!.Chat.Id,
                messageId: query.Message.MessageId,
                replyMarkup: GetMainMenu(query.Message.Chat.Id)
            );

            await _tgBot.AnswerCallbackQuery(query.Id);
        }

        public static async Task StopMonitoringFunpay(CallbackQuery query)
        {
            if (!_isMonitoringRunning)
            {
                await _tgBot.EditMessageReplyMarkup(
                    chatId: query.Message!.Chat.Id,
                    messageId: query.Message.MessageId,
                    replyMarkup: GetMainMenu(query.Message.Chat.Id)
                );
                await _tgBot.AnswerCallbackQuery(query.Id, "Уже отключено ⛔");
                return;
            }

            // 1) Сигнал на остановку фоновой задачи
            try { _monitorCts?.Cancel(); } catch { }

            // 2) Дождаться завершения Run
            try { if (_monitorTask != null) await _monitorTask; }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Console.WriteLine($"[STOP] Ошибка ожидания мониторинга: {ex.Message}"); }

            // 3) Освобождение ресурсов, держащих файлы сессий
            try
            {
                // Если у _orderProcessor есть Dispose — вызови
                (_orderProcessor as IDisposable)?.Dispose();
            }
            catch (Exception ex) { Console.WriteLine($"[STOP] Ошибка Dispose orderProcessor: {ex.Message}"); }
            finally
            {
                _orderProcessor = null;
            }

            if (_activeClients != null)
            {
                foreach (var (client, _) in _activeClients)
                {
                    try { client.Dispose(); } catch { }
                }
                _activeClients = null;
            }

            // 4) Очистка служебных полей
            _monitorTask = null;
            _monitorCts?.Dispose();
            _monitorCts = null;
            _isMonitoringRunning = false;

            // 5) UI
            await _tgBot.EditMessageReplyMarkup(
                chatId: query.Message!.Chat.Id,
                messageId: query.Message.MessageId,
                replyMarkup: GetMainMenu(query.Message.Chat.Id)
            );

            await _tgBot.AnswerCallbackQuery(query.Id);
        }

        private static async Task Run(CancellationToken token)
        {
            using var monitor = new FunPayChatMonitor(registerGoldenKey.GoldenKey, myUserId: 2195338);

            monitor.OnNewMessage += message =>
            {
                if (token.IsCancellationRequested) return;

                if (message.Type == MessageType.System)
                {
                    if (parseOrder.TryParseOrderPaid(message.Text, out var paid))
                    {
                        var order = new OrderData
                        {
                            Buyer = paid.Buyer,
                            OrderId = paid.OrderId,
                            Subcategory = paid.Subcategory,
                            Description = paid.Description
                        };

                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                token.ThrowIfCancellationRequested();

                                var (ok, error) = await _orderProcessor.ProcessOrderAsync(order);
                                if (ok)
                                {
                                    Console.WriteLine($"[ORDER COMPLETED] {order.Buyer}, {order.OrderId}, {order.Subcategory}, {order.Description}");
                                    await FunpayStepByStep.SendMessageByChatIdAsync(
                                        goldenKey: registerGoldenKey.GoldenKey,
                                        chatId: message.ChatId,
                                        text: "Ваш заказ успешно выполнен проверьте получение ✅  Будем рады видеть вас снова!❤️");
                                }
                                else
                                {
                                    Console.WriteLine($"[ORDER ERROR] {error}");
                                }
                            }
                            catch (OperationCanceledException) { }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[ERROR] Process/Send: {ex.Message}");
                            }
                        }, token);

                        return;
                    }
                }
                else if (message.Type == MessageType.Buyer && message.IsFirstInChat)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            token.ThrowIfCancellationRequested();

                            Console.WriteLine($"[BUYER] Отправлено привественное сообщение!");
                            await FunpayStepByStep.SendMessageByChatIdAsync(
                                goldenKey: registerGoldenKey.GoldenKey,
                                chatId: message.ChatId,
                                text: $"Привет, {message.Author}, ❤️\r\nПосле оплаты вы становитесь в очередь, звезды придут в течении 1 минуты❤️");
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[ERROR] Process/Send: {ex.Message}");
                        }
                    }, token);
                }
            };

            Console.WriteLine("Монитор запущен. Нажмите кнопку для остановки.");
            await monitor.StartAsync(token);
        }
    }

}

