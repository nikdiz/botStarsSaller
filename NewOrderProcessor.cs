using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TL;
using WTelegram;

namespace botStarsSaller
{
    public class NewOrderProcessor
    {
        private readonly List<(Client Client, string Phone)> _clients;
        private readonly HashSet<Client> _loggedIn = new HashSet<Client>();
        private int _nextIndex = 0;

        public NewOrderProcessor(List<(Client Client, string Phone)> clients)
        {
            _clients = clients ?? new List<(Client, string)>();
            if (_clients.Count == 0)
                throw new InvalidOperationException("Нет активных клиентов. Заполните clients.txt и активируйте нужные (flag=1).");
        }

        // Консольный аналог Post()
        public async Task<(bool ok, string error)> ProcessOrderAsync(OrderData order)
        {
            if (order == null)
                return (false, "Данные заказа пусты");

            if (order.Subcategory != "Telegram, Звёзды")
                return (false, "Неподдерживаемая подкатегория");

            var parsed = OrderParser.ParseOrderInfo(order.Description);
            if (parsed.StarsCount <= 0)
                return (false, "Не удалось распознать количество звёзд");
            if (parsed.Quantity < 1) parsed.Quantity = 1;
            if (parsed.Quantity > 30)
                return (false, "Слишком большое количество подарков, максимум 30.");

            Console.WriteLine($"\n⭐ Новый заказ: {parsed.StarsCount} звёзд для {parsed.Username}");

            // Берём следующего клиента по кругу
            var selected = GetNextClient();
            await EnsureLoggedInAsync(selected);

            // Отправляем подарки
            for (int i = 0; i < parsed.Quantity; i++)
            {
                var ok = await GiveGifts(selected.Client, parsed.Username, parsed.StarsCount);
                if (!ok)
                    return (false, "Не удалось отправить подарок выбранным клиентом");

                if (i < parsed.Quantity - 1)
                    await Task.Delay(TimeSpan.FromSeconds(2));
            }

            return (true, null);
        }

        private (Client Client, string Phone) GetNextClient()
        {
            var idx = _nextIndex;
            _nextIndex = (_nextIndex + 1) % _clients.Count;
            return _clients[idx];
        }

        private async Task EnsureLoggedInAsync((Client Client, string Phone) holder)
        {
            if (_loggedIn.Contains(holder.Client)) return;
            var me = await holder.Client.LoginUserIfNeeded();
            Console.WriteLine($"Logged in as {me.username} ({holder.Phone})");
            _loggedIn.Add(holder.Client);
        }

        private static async Task<bool> GiveGifts(Client client, string username, int starsCount)
        {
            try
            {
                var resolved = await client.Contacts_ResolveUsername(username);
                var userR = resolved.users.Values.First();
                var destPeer = new InputPeerUser(userR.id, userR.access_hash);

                long giftId;
                switch (starsCount)
                {
                    case 43: giftId = 5170144170496491616; break;
                    case 21: giftId = 5170250947678437525; break;
                    case 13: giftId = 5170233102089322756; break;
                    default:
                        Console.WriteLine("⚠️ Неверное количество звезд: " + starsCount);
                        return false;
                }

                var invoice = new InputInvoiceStarGift
                {
                    peer = destPeer,
                    gift_id = giftId,
                    message = new TextWithEntities { text = "Enjoy your gift!" },
                };

                var paymentFormBase = await client.Payments_GetPaymentForm(invoice, null);
                var paymentForm = paymentFormBase as Payments_PaymentFormStarGift;
                if (paymentForm == null)
                {
                    Console.WriteLine("❌ Не удалось получить PaymentForm");
                    return false;
                }

                var result = await client.Payments_SendStarsForm(paymentForm.form_id, invoice);
                Console.WriteLine("✅ Подарок отправлен! Результат: " + (result == null ? "null" : result.GetType().Name));
                return true;
            }
            catch (TL.RpcException ex) when (ex.Code == 420)
            {
                Console.WriteLine("⏳ FLOOD_WAIT: нужно подождать");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine("🚨 Ошибка при отправке подарка: " + ex.Message);
                return false;
            }
        }
    }
}