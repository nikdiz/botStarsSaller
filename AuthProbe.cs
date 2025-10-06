using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace botStarsSaller
{
    public enum MessageType
    {
        System,
        Buyer
    }

    public class ChatMessage
    {
        public long ChatId { get; set; }
        public long MessageId { get; set; }
        public MessageType Type { get; set; }
        public string Author { get; set; }
        public string Text { get; set; }
        public DateTime Timestamp { get; set; } // UTC
        public bool IsFirstInChat { get; set; }
    }

    public class FunPayChatMonitor : IDisposable
    {
        private readonly HttpClient _http;
        private readonly int _myUserId;
        private readonly Dictionary<long, long> _lastSeenByChat = new();
        private readonly HashSet<long> _initializedChats = new(); // Для отслеживания чатов, которые были на момент старта
        private readonly SemaphoreSlim _semaphore = new(1, 1);

        private static readonly Regex APP_DATA = new(@"data-app-data=['""]([^'""]+)['""]", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex CONTACT_ITEM = new(@"<a[^>]+class=""contact-item[^""]*""[^>]+data-id=""(\d+)""[^>]+data-node-msg=""(\d+)""[^>]+data-user-msg=""(\d+)""", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex ALL_TEXTS = new(@"<div\s+class=""chat-msg-text"">([\s\S]*?)</div>", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex BODY_INNER = new(@"<div\s+class=""chat-msg-body"">([\s\S]*?)</div>\s*</div>\s*</div>\s*</div>\s*$", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex IMG = new(@"class=""chat-img-link""\s+href=""([^""]+)""", RegexOptions.Compiled);
        private static readonly Regex SYSTEM_ALERT = new(@"<div[^>]*class=""[^""]*\balert\b[^""]*""[^>]*role=""alert""", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex SYSTEM_BADGE = new(@"<span[^>]*class=""[^""]*\bchat-msg-author-label\b[^""]*""", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex SYSTEM_CLASS = new(@"class=""[^""]*\b(chat-msg-system|system|alert)\b[^""]*""", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
        private static readonly Regex AUTHOR_LINK = new(@"<a[^>]+class=""chat-msg-author-link""[^>]*>([^<]+)</a>", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex AUTHOR_PLAIN = new(@"<div\s+class=""media-user-name"">\s*([^<\n\r]+)", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex SYSTEM_FUNPAY_HEADER_STRICT = new(@"<div\s+class=""media-user-name"">\s*FunPay\s*<span[^>]*class=""[^""]*\bchat-msg-author-label\b[^""]*""[\s\S]*?</span>", RegexOptions.Compiled | RegexOptions.Singleline);

        // Парс времени
        private static readonly Regex CHAT_MSG_DATE = new(@"<div\s+class=""chat-msg-date""[^>]*>([^<]+)</div>", RegexOptions.Compiled);
        private static readonly Regex CHAT_MSG_DATE_TITLE = new(@"<div\s+class=""chat-msg-date""[^>]*title=""([^""]+)""[^>]*>", RegexOptions.Compiled);

        // Кроссплатформенный способ получить московский часовой пояс
        public static TimeZoneInfo GetMoscowTimeZone()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Russian Standard Time"); }
            catch { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow"); }
        }

        private static readonly TimeZoneInfo MoscowTimeZone = GetMoscowTimeZone();

        // Время старта по серверу FunPay (UTC), чтобы не зависеть от часов машины
        private DateTime _serverStartUtc = DateTime.MinValue;
        private bool _serverTimeInitialized = false;

        public event Action<ChatMessage> OnNewMessage;

        public FunPayChatMonitor(string goldenKey, int myUserId)
        {
            _myUserId = myUserId;
            var cookies = new CookieContainer();
            var baseUri = new Uri("https://funpay.com/");
            cookies.Add(baseUri, new Cookie("golden_key", goldenKey) { Secure = true });
            cookies.Add(baseUri, new Cookie("cookie_prefs", "1") { Secure = true });
            cookies.Add(baseUri, new Cookie("locale", "ru") { Secure = true });
            cookies.Add(baseUri, new Cookie("cy", "RUB") { Secure = true });

            var handler = new HttpClientHandler
            {
                CookieContainer = cookies,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true,
                UseCookies = true
            };
            _http = new HttpClient(handler);
            _http.DefaultRequestHeaders.Add("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            _http.DefaultRequestHeaders.Add("accept", "application/json, text/javascript, */*; q=0.01");
            _http.DefaultRequestHeaders.Add("accept-language", "ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7");
            _http.Timeout = TimeSpan.FromSeconds(20);
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            Console.WriteLine("FunPay Chat Monitor запущен. Ожидание новых сообщений...");

            // --- ИНИЦИАЛИЗАЦИЯ watermark и server time ---
            string csrf = null;
            List<(long chatId, long topMsg)> chats = null;
            try
            {
                csrf = await GetCsrfAsync();
                if (!string.IsNullOrEmpty(csrf))
                {
                    chats = await GetChatsAsync(csrf);
                    if (chats != null)
                    {
                        foreach (var (chatId, topMsg) in chats)
                        {
                            _lastSeenByChat[chatId] = topMsg;
                            _initializedChats.Add(chatId);
                        }
                    }
                }
            }
            catch { /* ignore */ }
            // --- конец инициализации ---

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await _semaphore.WaitAsync(cancellationToken);

                    // Получаем CSRF токен (также может инициализировать серверное время)
                    csrf = await GetCsrfAsync();
                    if (string.IsNullOrEmpty(csrf))
                    {
                        Console.WriteLine("[ERROR] Не удалось получить CSRF токен");
                        await Task.Delay(2000, cancellationToken);
                        continue;
                    }

                    // Получаем список чатов
                    chats = await GetChatsAsync(csrf);
                    if (chats == null || chats.Count == 0)
                    {
                        await Task.Delay(1500, cancellationToken);
                        continue;
                    }

                    // Проверяем новые сообщения
                    await CheckNewMessagesAsync(csrf, chats, cancellationToken);

                    await Task.Delay(1500, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Ошибка в цикле мониторинга: {ex.Message}");
                    await Task.Delay(3000, cancellationToken);
                }
                finally
                {
                    _semaphore.Release();
                }
            }

            Console.WriteLine("FunPay Chat Monitor остановлен.");
        }

        private async Task<string> GetCsrfAsync()
        {
            try
            {
                var response = await _http.GetAsync("https://funpay.com/chat/");

                // Инициализируем время сервера по заголовку Date
                if (!_serverTimeInitialized)
                {
                    var srvDate = response.Headers.Date;
                    _serverStartUtc = srvDate.HasValue ? srvDate.Value.UtcDateTime : DateTime.UtcNow;
                    _serverTimeInitialized = true;
                    Console.WriteLine($"[INFO] Server start (UTC): {_serverStartUtc:O}");
                }

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[ERROR] Ошибка HTTP при получении CSRF: {response.StatusCode}");
                    return null;
                }

                var html = await response.Content.ReadAsStringAsync();
                var match = APP_DATA.Match(html);

                if (match.Success)
                {
                    var appData = match.Groups[1].Value;
                    var decodedAppData = WebUtility.HtmlDecode(appData);

                    var doc = JsonDocument.Parse(decodedAppData);
                    if (doc.RootElement.TryGetProperty("csrf-token", out var csrfEl))
                    {
                        return csrfEl.GetString();
                    }
                }

                Console.WriteLine("[ERROR] CSRF токен не найден в HTML");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Исключение при получении CSRF: {ex.Message}");
                if (!_serverTimeInitialized)
                {
                    _serverStartUtc = DateTime.UtcNow;
                    _serverTimeInitialized = true;
                }
                return null;
            }
        }

        private async Task<List<(long chatId, long topMsg)>> GetChatsAsync(string csrf)
        {
            try
            {
                var objects = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["type"] = "chat_bookmarks",
                        ["id"] = _myUserId,
                        ["tag"] = "cb_" + DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        ["data"] = false
                    }
                };

                var form = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string,string>("objects", JsonSerializer.Serialize(objects)),
                    new KeyValuePair<string,string>("request", "false"),
                    new KeyValuePair<string,string>("csrf_token", csrf)
                });

                var req = new HttpRequestMessage(HttpMethod.Post, "https://funpay.com/runner/") { Content = form };
                req.Headers.Referrer = new Uri("https://funpay.com/chat/");
                req.Headers.TryAddWithoutValidation("Origin", "https://funpay.com");
                req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

                var resp = await _http.SendAsync(req);
                var body = await resp.Content.ReadAsStringAsync();

                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("objects", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    return null;

                foreach (var obj in arr.EnumerateArray())
                {
                    if (obj.TryGetProperty("type", out var type) && type.GetString() == "chat_bookmarks" &&
                        obj.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
                        data.TryGetProperty("html", out var htmlEl))
                    {
                        var html = htmlEl.GetString();
                        var chats = new List<(long, long)>();

                        foreach (Match match in CONTACT_ITEM.Matches(html))
                        {
                            if (long.TryParse(match.Groups[1].Value, out var chatId) &&
                                long.TryParse(match.Groups[2].Value, out var nodeMsg) &&
                                long.TryParse(match.Groups[3].Value, out var userMsg))
                            {
                                var topMsg = Math.Max(nodeMsg, userMsg);
                                chats.Add((chatId, topMsg));
                            }
                        }
                        return chats;
                    }
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        private async Task CheckNewMessagesAsync(string csrf, List<(long chatId, long topMsg)> chats, CancellationToken cancellationToken)
        {
            // Для новых чатов, появившихся после старта, watermark = 0
            foreach (var (chatId, topMsg) in chats)
            {
                if (!_lastSeenByChat.ContainsKey(chatId))
                {
                    if (_initializedChats.Contains(chatId))
                        continue; // этот чат уже был инициализирован при старте
                    _lastSeenByChat[chatId] = 0; // новый чат — читаем все сообщения
                }
            }

            // Находим чаты с изменениями
            var changed = chats.Where(c => _lastSeenByChat.TryGetValue(c.chatId, out var lastSeen) && c.topMsg > lastSeen)
                               .OrderByDescending(c => c.topMsg)
                               .ToList();

            if (changed.Count == 0) return;

            // Запрашиваем новые сообщения
            var objects = new List<Dictionary<string, object>>();
            int i = 0;
            foreach (var (chatId, _) in changed)
            {
                objects.Add(new Dictionary<string, object>
                {
                    ["type"] = "chat_node",
                    ["id"] = chatId,
                    ["tag"] = "cn_" + (i++),
                    ["data"] = false
                });
            }

            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string,string>("objects", JsonSerializer.Serialize(objects)),
                new KeyValuePair<string,string>("request", "false"),
                new KeyValuePair<string,string>("csrf_token", csrf)
            });

            var req = new HttpRequestMessage(HttpMethod.Post, "https://funpay.com/runner/") { Content = form };
            req.Headers.Referrer = new Uri("https://funpay.com/chat/");
            req.Headers.TryAddWithoutValidation("Origin", "https://funpay.com");
            req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

            var resp = await _http.SendAsync(req, cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);

            // Парсим ответ
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("objects", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    return;

                foreach (var obj in arr.EnumerateArray())
                {
                    if (!obj.TryGetProperty("type", out var objType) || objType.GetString() != "chat_node") continue;
                    if (!obj.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) continue;

                    long chatId = 0;
                    if (data.TryGetProperty("node", out var node) && node.ValueKind == JsonValueKind.Object &&
                        node.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
                        chatId = idEl.GetInt64();

                    if (chatId == 0) continue;

                    long watermark = _lastSeenByChat.TryGetValue(chatId, out var lastSeen) ? lastSeen : 0;

                    if (!data.TryGetProperty("messages", out var msgs) || msgs.ValueKind != JsonValueKind.Array)
                        continue;

                    var messageList = new List<(long id, string html)>();
                    foreach (var msg in msgs.EnumerateArray())
                    {
                        if (!msg.TryGetProperty("id", out var midEl) || midEl.ValueKind != JsonValueKind.Number) continue;
                        long mid = midEl.GetInt64();
                        if (!msg.TryGetProperty("html", out var htmlEl) || htmlEl.ValueKind != JsonValueKind.String) continue;
                        string mhtml = WebUtility.HtmlDecode(htmlEl.GetString() ?? "");
                        messageList.Add((mid, mhtml));
                    }

                    // Для восстановления даты, если в сообщении только время (используем дату предыдущего сообщения)
                    DateTime? lastDateMsk = null; // локальная дата (MSK) последнего успешно распарсенного сообщения

                    // Новый цикл — с учётом первого системного и фильтра времени
                    var orderedMessages = messageList.OrderBy(x => x.id).ToList();
                    for (int idx = 0; idx < orderedMessages.Count; idx++)
                    {
                        var (mid, mhtml) = orderedMessages[idx];
                        if (mid <= watermark) continue; // только новые

                        var (msgType, author, text) = ClassifyFromHtml(mhtml);

                        // Парсим время; если не смогли — пропускаем (никаких fallback на UtcNow)
                        DateTime? msgUtc = ParseMessageDateToUtc(mhtml, lastDateMsk);
                        if (msgUtc == null)
                            continue;

                        // Обновляем lastDateMsk для следующего сообщения (только дата в MSK)
                        var msk = TimeZoneInfo.ConvertTimeFromUtc(msgUtc.Value, MoscowTimeZone);
                        lastDateMsk = new DateTime(msk.Year, msk.Month, msk.Day, 0, 0, 0, DateTimeKind.Unspecified);

                        // Фильтрация по времени старта, основанному на времени сервера (UTC)
                        if (msgUtc.Value < _serverStartUtc)
                            continue;

                        var message = new ChatMessage
                        {
                            ChatId = chatId,
                            MessageId = mid,
                            Type = msgType,
                            Author = author,
                            Text = text,
                            Timestamp = msgUtc.Value, // UTC
                            IsFirstInChat = (idx == 0)
                        };

                        try
                        {
                            OnNewMessage?.Invoke(message);
                        }
                        catch (Exception cbEx)
                        {
                            Console.WriteLine($"[ERROR] Ошибка обработчика OnNewMessage: {cbEx.Message}");
                        }

                        // watermark обновляем только после обработки сообщения
                        watermark = mid;
                    }

                    // watermark обновляем только если он увеличился
                    if (watermark > (_lastSeenByChat.TryGetValue(chatId, out var prev) ? prev : 0))
                        _lastSeenByChat[chatId] = watermark;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Ошибка парсинга сообщений: {ex.Message}");
            }
        }

        private (MessageType type, string author, string text) ClassifyFromHtml(string block)
        {
            string text;
            var texts = ALL_TEXTS.Matches(block);
            if (texts.Count > 0)
            {
                var last = texts[texts.Count - 1].Groups[1].Value;
                text = Clean(last);
            }
            else
            {
                var mi = IMG.Match(block);
                if (mi.Success) text = $"[IMAGE] {mi.Groups[1].Value}";
                else
                {
                    var body = BODY_INNER.Match(block);
                    var bodyHtml = body.Success ? body.Groups[1].Value : block;
                    text = Clean(bodyHtml);
                }
            }

            string author = null;
            var m1 = AUTHOR_LINK.Match(block);
            if (m1.Success) author = Clean(m1.Groups[1].Value);
            else
            {
                var m2 = AUTHOR_PLAIN.Match(block);
                if (m2.Success) author = Clean(m2.Groups[1].Value);
            }
            if (string.IsNullOrWhiteSpace(author)) author = "Покупатель";

            bool isSystem = SYSTEM_ALERT.IsMatch(block)
                || SYSTEM_BADGE.IsMatch(block)
                || SYSTEM_CLASS.IsMatch(block)
                || SYSTEM_FUNPAY_HEADER_STRICT.IsMatch(block);
            if (isSystem) author = "FunPay";

            return (isSystem ? MessageType.System : MessageType.Buyer, author, text);
        }

        private static string Clean(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return "";
            html = html.Replace("<br>", "\n");
            return WebUtility.HtmlDecode(Regex.Replace(html, "<.*?>", "").Trim());
        }

        // Парсинг времени сообщения из HTML в UTC; если не удалось — return null
        private DateTime? ParseMessageDateToUtc(string html, DateTime? lastDateMsk)
        {
            // 1) Пытаемся взять из title="12 сентября, 18:50:22"
            var t = CHAT_MSG_DATE_TITLE.Match(html);
            if (t.Success)
            {
                var titleVal = WebUtility.HtmlDecode(t.Groups[1].Value.Trim());
                if (TryParseRusTitleDateTimeMsk(titleVal, lastDateMsk, out DateTime utcFromTitle))
                    return utcFromTitle;
            }

            // 2) Смотрим контент между тегами (встречаются "HH:MM:SS" или "DD.MM.YY")
            var m = CHAT_MSG_DATE.Match(html);
            if (m.Success)
            {
                var inner = WebUtility.HtmlDecode(m.Groups[1].Value.Trim());

                // Формат времени HH:MM:SS
                if (Regex.IsMatch(inner, @"^\d{2}:\d{2}:\d{2}$"))
                {
                    if (lastDateMsk == null) return null; // без даты — пропускаем
                    var tp = inner.Split(':').Select(int.Parse).ToArray();
                    var dtLocal = new DateTime(lastDateMsk.Value.Year, lastDateMsk.Value.Month, lastDateMsk.Value.Day, tp[0], tp[1], tp[2], DateTimeKind.Unspecified);
                    return TimeZoneInfo.ConvertTimeToUtc(dtLocal, MoscowTimeZone);
                }

                // Формат даты DD.MM.YY (без времени -> 00:00:00)
                if (Regex.IsMatch(inner, @"^\d{2}\.\d{2}\.\d{2}$"))
                {
                    var dp = inner.Split('.').Select(int.Parse).ToArray();
                    int year = 2000 + dp[2];
                    var dtLocal = new DateTime(year, dp[1], dp[0], 0, 0, 0, DateTimeKind.Unspecified);
                    return TimeZoneInfo.ConvertTimeToUtc(dtLocal, MoscowTimeZone);
                }
            }

            // Не удалось распарсить — пропускаем
            return null;
        }

        // Парсим строку вида "12 сентября, 18:50:22" (без года) в UTC
        private bool TryParseRusTitleDateTimeMsk(string title, DateTime? lastDateMsk, out DateTime utc)
        {
            utc = default;

            // Разбор "DD <месяц>, HH:MM:SS"
            var rm = Regex.Match(title, @"^\s*(\d{1,2})\s+([А-Яа-яёЁ]+)\s*,\s*(\d{2}):(\d{2}):(\d{2})\s*$");
            if (!rm.Success) return false;

            int day = int.Parse(rm.Groups[1].Value);
            string monName = rm.Groups[2].Value.ToLowerInvariant();
            int hour = int.Parse(rm.Groups[3].Value);
            int min = int.Parse(rm.Groups[4].Value);
            int sec = int.Parse(rm.Groups[5].Value);

            var months = new Dictionary<string, int>
            {
                {"января", 1}, {"февраля", 2}, {"марта", 3}, {"апреля", 4}, {"мая", 5}, {"июня", 6},
                {"июля", 7}, {"августа", 8}, {"сентября", 9}, {"октября", 10}, {"ноября", 11}, {"декабря", 12}
            };
            if (!months.TryGetValue(monName, out int month)) return false;

            // Выбор года: берем из lastDateMsk если есть, иначе из серверного старта, переведённого в MSK
            int year;
            if (lastDateMsk.HasValue)
            {
                year = lastDateMsk.Value.Year;
            }
            else
            {
                var startMsk = TimeZoneInfo.ConvertTimeFromUtc(_serverStartUtc, MoscowTimeZone);
                year = startMsk.Year;
            }

            try
            {
                var dtLocal = new DateTime(year, month, day, hour, min, sec, DateTimeKind.Unspecified);
                utc = TimeZoneInfo.ConvertTimeToUtc(dtLocal, MoscowTimeZone);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            _http?.Dispose();
            _semaphore?.Dispose();
        }
    }
}