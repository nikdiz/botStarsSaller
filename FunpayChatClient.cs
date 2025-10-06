using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HtmlAgilityPack;

namespace botStarsSaller
{
    public static class FunpayStepByStep
    {
        // 1. Получить nodeKey по chatId
        public static async Task<string> GetNodeKeyByChatIdAsync(string goldenKey, long chatId)
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                CookieContainer = new CookieContainer(),
                AllowAutoRedirect = true,
                UseCookies = true
            };
            handler.CookieContainer.Add(new Cookie("golden_key", goldenKey, "/", "funpay.com"));
            handler.CookieContainer.Add(new Cookie("cookie_prefs", "1", "/", "funpay.com"));
            handler.CookieContainer.Add(new Cookie("locale", "ru", "/", "funpay.com"));
            handler.CookieContainer.Add(new Cookie("cy", "RUB", "/", "funpay.com"));

            using var http = new HttpClient(handler);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/javascript, */*; q=0.01");
            http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7");

            // Получаем csrf-токен
            var chatUrl = $"https://funpay.com/chat/?node={chatId}";
            var get = await http.GetAsync(chatUrl);
            var html = await get.Content.ReadAsStringAsync();

            var mApp = Regex.Match(html, @"data-app-data=['""]([^'""]+)['""]", RegexOptions.Singleline);
            if (!mApp.Success) throw new Exception("data-app-data не найден");
            var appData = WebUtility.HtmlDecode(mApp.Groups[1].Value);
            var mCsrf = Regex.Match(appData, @"""csrf-token""\s*:\s*""([^""]+)""", RegexOptions.Singleline);
            if (!mCsrf.Success) throw new Exception("csrf-токен не найден");
            var csrf = mCsrf.Groups[1].Value;

            // Формируем chat_node запрос
            var tag = Guid.NewGuid().ToString("N").Substring(0, 8);
            var chatNodeObj = new Dictionary<string, object>
            {
                ["type"] = "chat_node",
                ["id"] = chatId,
                ["tag"] = tag,
                ["data"] = false
            };
            var objects = new object[] { chatNodeObj };

            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string,string>("objects", JsonSerializer.Serialize(objects)),
                new KeyValuePair<string,string>("csrf_token", csrf)
            });

            var req = new HttpRequestMessage(HttpMethod.Post, "https://funpay.com/runner/") { Content = form };
            req.Headers.Referrer = new Uri(chatUrl);
            req.Headers.TryAddWithoutValidation("Origin", "https://funpay.com");
            req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

            var resp = await http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("objects", out var objectsElem) || objectsElem.ValueKind != JsonValueKind.Array)
                throw new Exception("В ответе нет массива 'objects'");

            foreach (var obj in objectsElem.EnumerateArray())
            {
                if (obj.TryGetProperty("type", out var t) && t.GetString() == "chat_node")
                {
                    if (obj.TryGetProperty("data", out var dataElem) && dataElem.ValueKind == JsonValueKind.Object)
                    {
                        if (dataElem.TryGetProperty("node", out var nodeElem) && nodeElem.ValueKind == JsonValueKind.Object)
                        {
                            if (nodeElem.TryGetProperty("name", out var nameElem) && nameElem.ValueKind == JsonValueKind.String)
                            {
                                return nameElem.GetString(); // nodeKey
                            }
                        }
                    }
                }
            }
            throw new Exception("Не найден nodeKey для чата");
        }

        // 2. Парсим userId и buyerUserId из nodeKey
        public static (int myUserId, int buyerUserId) ParseNodeKey(string nodeKey)
        {
            var parts = nodeKey.Split('-');
            if (parts.Length == 3 && parts[0] == "users")
            {
                return (int.Parse(parts[1]), int.Parse(parts[2]));
            }
            throw new Exception("Некорректный nodeKey");
        }

        // 3. Получаем last_message через парсинг HTML
        public static async Task<long> GetLastMessageIdFromHtmlAsync(string goldenKey, int myUserId, long chatId)
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                CookieContainer = new CookieContainer(),
                AllowAutoRedirect = true,
                UseCookies = true
            };
            handler.CookieContainer.Add(new Cookie("golden_key", goldenKey, "/", "funpay.com"));
            handler.CookieContainer.Add(new Cookie("cookie_prefs", "1", "/", "funpay.com"));
            handler.CookieContainer.Add(new Cookie("locale", "ru", "/", "funpay.com"));
            handler.CookieContainer.Add(new Cookie("cy", "RUB", "/", "funpay.com"));

            using var http = new HttpClient(handler);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/javascript, */*; q=0.01");
            http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7");

            // Получаем csrf-токен
            var chatUrl = $"https://funpay.com/chat/?node={chatId}";
            var get = await http.GetAsync(chatUrl);
            var html = await get.Content.ReadAsStringAsync();

            var mApp = Regex.Match(html, @"data-app-data=['""]([^'""]+)['""]", RegexOptions.Singleline);
            if (!mApp.Success) throw new Exception("data-app-data не найден");
            var appData = WebUtility.HtmlDecode(mApp.Groups[1].Value);
            var mCsrf = Regex.Match(appData, @"""csrf-token""\s*:\s*""([^""]+)""", RegexOptions.Singleline);
            if (!mCsrf.Success) throw new Exception("csrf-токен не найден");
            var csrf = mCsrf.Groups[1].Value;

            // Формируем chat_bookmarks запрос
            var tag = Guid.NewGuid().ToString("N").Substring(0, 8);
            var chatBookmarksObj = new Dictionary<string, object>
            {
                ["type"] = "chat_bookmarks",
                ["id"] = myUserId.ToString(),
                ["tag"] = tag,
                ["data"] = false
            };
            var objects = new object[] { chatBookmarksObj };

            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string,string>("objects", JsonSerializer.Serialize(objects)),
                new KeyValuePair<string,string>("csrf_token", csrf)
            });

            var req = new HttpRequestMessage(HttpMethod.Post, "https://funpay.com/runner/") { Content = form };
            req.Headers.Referrer = new Uri(chatUrl);
            req.Headers.TryAddWithoutValidation("Origin", "https://funpay.com");
            req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

            var resp = await http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("objects", out var objectsElem) || objectsElem.ValueKind != JsonValueKind.Array)
                throw new Exception("В ответе нет массива 'objects'");

            foreach (var obj in objectsElem.EnumerateArray())
            {
                if (obj.TryGetProperty("type", out var t) && t.GetString() == "chat_bookmarks")
                {
                    if (obj.TryGetProperty("data", out var dataElem) && dataElem.ValueKind == JsonValueKind.Object)
                    {
                        if (dataElem.TryGetProperty("html", out var htmlElem) && htmlElem.ValueKind == JsonValueKind.String)
                        {
                            var htmlStr = htmlElem.GetString();
                            return ExtractLastMessageIdFromHtml(htmlStr, chatId);
                        }
                    }
                }
            }
            throw new Exception("Не найден last_message для чата");
        }

        // 4. Парсинг last_message из HTML
        public static long ExtractLastMessageIdFromHtml(string html, long chatId)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            var nodes = doc.DocumentNode.SelectNodes("//a[@class='contact-item']");
            if (nodes != null)
            {
                foreach (var node in nodes)
                {
                    var idAttr = node.GetAttributeValue("data-id", "");
                    if (idAttr == chatId.ToString())
                    {
                        var msgId = node.GetAttributeValue("data-node-msg", "");
                        if (long.TryParse(msgId, out var lastMsgId))
                            return lastMsgId;
                    }
                }
            }
            throw new Exception("Не найден last_message для чата в html");
        }

        // 5. Отправка сообщения (как браузер)
        public static async Task Step6_SendWithAutoLastMessageAsync(
            string goldenKey,
            int myUserId,
            int buyerUserId,
            long chatId,
            string text)
        {
            long lastMsgId = await GetLastMessageIdFromHtmlAsync(goldenKey, myUserId, chatId);

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                CookieContainer = new CookieContainer(),
                AllowAutoRedirect = true,
                UseCookies = true
            };
            handler.CookieContainer.Add(new Cookie("golden_key", goldenKey, "/", "funpay.com"));
            handler.CookieContainer.Add(new Cookie("cookie_prefs", "1", "/", "funpay.com"));
            handler.CookieContainer.Add(new Cookie("locale", "ru", "/", "funpay.com"));
            handler.CookieContainer.Add(new Cookie("cy", "RUB", "/", "funpay.com"));

            using var http = new HttpClient(handler);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/javascript, */*; q=0.01");
            http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7");

            // Получаем csrf-токен
            var chatUrl = $"https://funpay.com/chat/?node={chatId}";
            var get = await http.GetAsync(chatUrl);
            var html = await get.Content.ReadAsStringAsync();

            var mApp = Regex.Match(html, @"data-app-data=['""]([^'""]+)['""]", RegexOptions.Singleline);
            if (!mApp.Success) throw new Exception("data-app-data не найден");
            var appData = WebUtility.HtmlDecode(mApp.Groups[1].Value);
            var mCsrf = Regex.Match(appData, @"""csrf-token""\s*:\s*""([^""]+)""", RegexOptions.Singleline);
            if (!mCsrf.Success) throw new Exception("csrf-токен не найден");
            var csrf = mCsrf.Groups[1].Value;

            // Формируем tags
            string Tag() => Guid.NewGuid().ToString("N").Substring(0, 8);

            // Формируем objects
            var ordersObj = new Dictionary<string, object>
            {
                ["type"] = "orders_counters",
                ["id"] = myUserId.ToString(),
                ["tag"] = Tag(),
                ["data"] = false
            };

            var chatNodeObj = new Dictionary<string, object>
            {
                ["type"] = "chat_node",
                ["id"] = $"users-{myUserId}-{buyerUserId}",
                ["tag"] = Tag(),
                ["data"] = new Dictionary<string, object>
                {
                    ["node"] = $"users-{myUserId}-{buyerUserId}",
                    ["last_message"] = lastMsgId,
                    ["content"] = text
                }
            };

            var chatBookmarksObj = new Dictionary<string, object>
            {
                ["type"] = "chat_bookmarks",
                ["id"] = myUserId.ToString(),
                ["tag"] = Tag(),
                ["data"] = new object[] { new object[] { chatId, lastMsgId } }
            };

            var objects = new object[] { ordersObj, chatNodeObj, chatBookmarksObj };

            // Формируем поле request
            var requestObj = new Dictionary<string, object>
            {
                ["action"] = "chat_message",
                ["data"] = new Dictionary<string, object>
                {
                    ["node"] = $"users-{myUserId}-{buyerUserId}",
                    ["last_message"] = lastMsgId,
                    ["content"] = text
                }
            };
            var requestJson = JsonSerializer.Serialize(requestObj);

            // Формируем form-data
            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string,string>("objects", JsonSerializer.Serialize(objects)),
                new KeyValuePair<string,string>("request", requestJson),
                new KeyValuePair<string,string>("csrf_token", csrf)
            });

            var reqSend = new HttpRequestMessage(HttpMethod.Post, "https://funpay.com/runner/") { Content = form };
            reqSend.Headers.Referrer = new Uri(chatUrl);
            reqSend.Headers.TryAddWithoutValidation("Origin", "https://funpay.com");
            reqSend.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

            Console.WriteLine("[STEP6] POST /runner (chat_message, auto last_message)");
            var respSend = await http.SendAsync(reqSend);
            var bodySend = await respSend.Content.ReadAsStringAsync();
            Console.WriteLine($"[STEP6] code={(int)respSend.StatusCode}");
            Console.WriteLine($"[STEP6] body:\n{(bodySend.Length > 800 ? bodySend.Substring(0, 800) + "..." : bodySend)}");
        }

        // 6. Универсальный метод: только chatId и goldenKey
        public static async Task SendMessageByChatIdAsync(
            string goldenKey,
            long chatId,
            string text)
        {
            // 1. Получаем nodeKey
            string nodeKey = await GetNodeKeyByChatIdAsync(goldenKey, chatId);
            var (myUserId, buyerUserId) = ParseNodeKey(nodeKey);

            // 2. Отправляем сообщение
            await Step6_SendWithAutoLastMessageAsync(
                goldenKey: goldenKey,
                myUserId: myUserId,
                buyerUserId: buyerUserId,
                chatId: chatId,
                text: text
            );
        }
    }
}