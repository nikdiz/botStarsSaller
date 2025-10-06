using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TL;
using WTelegram;

namespace botStarsSaller
{
    public class NewOrderProcessor
    {
        private static bool _useClient1ForNextOrder = true;
        private static Client _client1;
        private static Client _client2;
        private static bool _isLoggedIn1 = false;
        private static bool _isLoggedIn2 = false;

        private static string SessionsDir
        {
            get
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Sessions");
                if (!Directory.Exists(path))
                    Directory.CreateDirectory(path);
                return path;
            }
        }

        private static Client Client1 => _client1 ??= new Client(Config1);
        private static Client Client2 => _client2 ??= new Client(Config2);

        // Консольный аналог Post()
        public async Task<(bool ok, string error)> ProcessOrderAsync(OrderData order)
        {
            if (order == null)
                return (false, "Данные заказа пусты");

            if (order.Subcategory != "Telegram, Звёзды")
                return (false, "Неподдерживаемая подкатегория");

            // Используем твой OrderParser
            var parsed = OrderParser.ParseOrderInfo(order.Description);
            if (parsed.StarsCount <= 0)
                return (false, "Не удалось распознать количество звёзд");
            if (parsed.Quantity < 1) parsed.Quantity = 1;
            if (parsed.Quantity > 30)
                return (false, "Слишком большое количество подарков, максимум 30.");

            Console.WriteLine($"\n⭐ Новый заказ: {parsed.StarsCount} звёзд для {parsed.Username}");

            // Выбираем клиента на заказ
            var preferredClient = _useClient1ForNextOrder ? Client1 : Client2;
            var fallbackClient = _useClient1ForNextOrder ? Client2 : Client1;
            var preferredPhone = _useClient1ForNextOrder ? Config1("phone_number") : Config2("phone_number");
            var fallbackPhone = _useClient1ForNextOrder ? Config2("phone_number") : Config1("phone_number");

            // Меняем флаг для следующего заказа
            _useClient1ForNextOrder = true;// !_useClient1ForNextOrder;

            // Логиним клиентов если нужно
            if (preferredClient == Client1 && !_isLoggedIn1)
            {
                var me1 = await Client1.LoginUserIfNeeded();
                Console.WriteLine($"Client1: Logged in as {me1.username}");
                _isLoggedIn1 = true;
            }
            else if (preferredClient == Client2 && !_isLoggedIn2)
            {
                var me2 = await Client2.LoginUserIfNeeded();
                Console.WriteLine($"Client2: Logged in as {me2.username}");
                _isLoggedIn2 = true;
            }

            // Отправляем подарки
            for (int i = 0; i < parsed.Quantity; i++)
            {
                bool sent = await TrySendGift(parsed.Username, parsed.StarsCount, preferredClient, fallbackClient, preferredPhone, fallbackPhone);
                if (!sent)
                    return (false, "Не удалось отправить подарок ни с одного аккаунта");

                if (i < parsed.Quantity - 1)
                    await Task.Delay(TimeSpan.FromSeconds(2));
            }

            return (true, null);
        }

        private static async Task<bool> TrySendGift(string username, int starsCount, Client preferred, Client fallback, string preferredPhone, string fallbackPhone)
        {
            if (await GiveGifts(preferred, username, starsCount))
                return true;

            Console.WriteLine($"⚠️ Ошибка при отправке через {preferredPhone}, пробуем резервный аккаунт {fallbackPhone}");
            return await GiveGifts(fallback, username, starsCount);
        }

        private static async Task<bool> GiveGifts(Client client, string username, int starsCount)
        {
            try
            {
                var resolved = await client.Contacts_ResolveUsername(username);
                var userR = resolved.users.Values.First();
                var destPeer = new InputPeerUser(userR.id, userR.access_hash);

                long giftId = starsCount switch
                {
                    43 => 5170144170496491616,
                    21 => 5170250947678437525,
                    13 => 5170233102089322756,
                    _ => 0
                };

                if (giftId == 0)
                {
                    Console.WriteLine($"⚠️ Неверное количество звезд: {starsCount}");
                    return false;
                }

                var invoice = new InputInvoiceStarGift
                {
                    peer = destPeer,
                    gift_id = giftId,
                    message = new TextWithEntities { text = "Enjoy your gift!" },
                };

                var paymentFormBase = await client.Payments_GetPaymentForm(invoice, null);
                if (paymentFormBase is not Payments_PaymentFormStarGift paymentForm)
                {
                    Console.WriteLine("❌ Не удалось получить PaymentForm");
                    return false;
                }

                var result = await client.Payments_SendStarsForm(paymentForm.form_id, invoice);
                Console.WriteLine($"✅ Подарок отправлен! Результат: {result?.GetType().Name}");
                return true;
            }
            catch (TL.RpcException ex) when (ex.Code == 420)
            {
                Console.WriteLine("⏳ FLOOD_WAIT: нужно подождать");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"🚨 Ошибка при отправке подарка: {ex.Message}");
                return false;
            }
        }

        private static string Config1(string what) => what switch
        {
            "session_pathname" => Path.Combine(SessionsDir, "session1.session"),
            "api_id" => "27292048",
            "api_hash" => "3a0448e004d0f8e50c1e9a213216e967",
            "phone_number" => "+79165823873",
            _ => null
        };

        private static string Config2(string what) => what switch
        {
            "session_pathname" => Path.Combine(SessionsDir, "session2.session"),
            "api_id" => "20814439",
            "api_hash" => "0f6ee02fd3a96b954cfd3e6db96fe568",
            "phone_number" => "+79933010115",
            _ => null
        };
    }
}