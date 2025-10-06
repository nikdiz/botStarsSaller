using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace botStarsSaller
{
    public static class OrderParser
    {
        public static ParsedOrderInfo ParseOrderInfo(string description)
        {
            var result = new ParsedOrderInfo();

            // Найти все совпадения числа перед словом "звёзд"
            var matches = Regex.Matches(description, @"(\d+)\s*зв(е|ё)зд(а)?");

            // Берём только первое совпадение
            if (matches.Count > 0)
            {
                var starsCount = int.Parse(matches[0].Groups[1].Value);

                // Ограничение: допустимый диапазон звёзд (например, 1–100)
                if (starsCount < 1 || starsCount > 100)
                    starsCount = 0;

                result.StarsCount = starsCount;
            }

            // 2. Количество штук (если есть)
            var matchQty = Regex.Match(description, @"(\d+)\s*шт\.?", RegexOptions.IgnoreCase);
            if (matchQty.Success)
                result.Quantity = int.Parse(matchQty.Groups[1].Value);

            // 3. Username (последнее слово)
            string[] parts = description.Split(new char[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
            {
                string lastWord = parts[parts.Length - 1].Trim();
                lastWord = lastWord.TrimEnd('.'); // убираем только точку в конце
                result.Username = lastWord.StartsWith("@") ? lastWord.Substring(1) : lastWord;
            }

            return result;
        }
    }
}
