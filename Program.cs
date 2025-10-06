using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TL;

namespace botStarsSaller
{
    internal class Program
    {
        static NewOrderProcessor _orderProcessor = new NewOrderProcessor();
        

        static void Main(string[] args)
        {
            try { Run().GetAwaiter().GetResult(); }
            catch (Exception ex) { Console.WriteLine(ex); }
        }
        private static async Task Run()
        {
            var register = new Register();

            using var monitor = new FunPayChatMonitor(register.GoldenKey, myUserId: 2195338);

            // Подписываемся на новые сообщения
            monitor.OnNewMessage += message =>
            {
                /*
                Console.WriteLine($"=== НОВОЕ СООБЩЕНИЕ ===");
                Console.WriteLine($"Чат ID: {message.ChatId}");
                Console.WriteLine($"Сообщение ID: {message.MessageId}");
                Console.WriteLine($"Тип: {message.Type}");
                Console.WriteLine($"Автор: {message.Author}");
                Console.WriteLine($"Текст: {message.Text}");
                Console.WriteLine($"Время: {message.Timestamp:HH:mm:ss}");
                Console.WriteLine($"Первое в чате: {message.IsFirstInChat}");
                Console.WriteLine("========================");*/

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
                                await Task.Delay(1000); // задержка
                                var (ok, error) = await _orderProcessor.ProcessOrderAsync(order);
                                if (ok)
                                {
                                    Console.WriteLine($"[ORDER] Заказ выполнен: {order.Buyer}, {order.Description}");
                                    Console.WriteLine($"[ORDER COMPLETED] {order.Buyer}, {order.OrderId}, {order.Subcategory}, {order.Description}");
                                    await FunpayStepByStep.SendMessageByChatIdAsync(
                                    goldenKey: register.GoldenKey,
                                    chatId: message.ChatId,
                                    text: "Ваш заказ успешно выполнен проверьте получение ✅  Будем рады видеть вас снова!❤️");
                                }
                                else
                                {
                                    Console.WriteLine($"[ORDER ERROR] {error}");
                                }
                                /*
                                Console.WriteLine($"[ORDER] Покупатель: {order.Buyer}");
                                Console.WriteLine($"[ORDER] Номер заказа: {order.OrderId}");
                                Console.WriteLine($"[ORDER] Подкатегория: {order.Subcategory}");
                                Console.WriteLine($"[ORDER] Описание: {order.Description}");*/
                                
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[ERROR] Process/Send: {ex.Message}");
                            }
                        });

                        return;
                    }
                    else
                    {
                        //Console.WriteLine("Не удалось распарсить");
                    }
                }
                else if (message.Type == MessageType.Buyer)
                {
                    if (message.IsFirstInChat)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                Console.WriteLine($"[BUYER] Отправлено привественное сообщение!");
                                await FunpayStepByStep.SendMessageByChatIdAsync(
                                    goldenKey: register.GoldenKey,
                                    chatId: message.ChatId,
                                    text: $"Привет, {message.Author}, ❤️\r\nПосле оплаты вы становитесь в очередь, звезды придут в течении 1 минуты❤️");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[ERROR] Process/Send: {ex.Message}");
                            }
                        });
                    }
                }
            };

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };

            Console.WriteLine("Монитор запущен. Отправьте сообщение в любой чат для тестирования.");
            Console.WriteLine("Нажмите Ctrl+C для остановки.");

            await monitor.StartAsync(cts.Token);
        }       
    }
}

