using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace botStarsSaller
{
    internal class parseOrder
    {
        public static bool TryParseOrderPaid(string text, out OrderData info)
        {
            info = null;

            // Берём только первую строку (на случай, если есть переносы)
            var firstLine = text.Split('\n')[0].Trim();

            // ВЫВОДИМ строку, которую пытаемся парсить
            Console.WriteLine("=== [ORDER PARSER INPUT] ===");
            Console.WriteLine(firstLine);
            Console.WriteLine("=== [END ORDER PARSER INPUT] ===");

            var rx = new Regex(
                @"Покупатель\s+(?<buyer>.+?)\s+оплатил\s+заказ\s+#(?<order>\w+)\.\s+(?<details>.+)",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

            var m = rx.Match(firstLine);
            if (!m.Success) return false;

            var buyer = m.Groups["buyer"].Value.Trim();
            var order = m.Groups["order"].Value.Trim();
            var details = m.Groups["details"].Value.Trim();

            // Убираем точку в конце, если есть
            if (details.EndsWith(".")) details = details[..^1];

            // Категория — первые два элемента через запятую
            var items = details.Split(',')
                               .Select(s => s.Trim())
                               .Where(s => s.Length > 0)
                               .ToList();

            var subcat = items.Count >= 2 ? string.Join(", ", items.Take(2)) :
                       items.Count == 1 ? items[0] : "";

            var desc = items.Count > 2 ? string.Join(", ", items.Skip(2)) + "." :
                      items.Count == 0 ? "" : items.Count == 2 ? "" : items[1] + ".";

            info = new OrderData
            {
                Buyer = buyer,
                OrderId = order,
                Subcategory = subcat,
                Description = desc
            };
            return true;
        }
    }
}
