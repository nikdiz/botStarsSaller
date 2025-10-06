using System;
using System.IO;

namespace botStarsSaller
{
    internal class Register
    {
        private const string GoldenKeyFile = "goldenkey.txt";
        public string GoldenKey { get; private set; }

        public Register()
        {
            GoldenKey = GetOrRequestGoldenKey();
        }

        private string GetOrRequestGoldenKey()
        {
            if (File.Exists(GoldenKeyFile))
            {
                var key = File.ReadAllText(GoldenKeyFile).Trim();
                if (!string.IsNullOrWhiteSpace(key))
                    return key;
            }

            Console.Write("Введите ваш golden key: ");
            var inputKey = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(inputKey))
                throw new Exception("Golden key не может быть пустым!");

            File.WriteAllText(GoldenKeyFile, inputKey);
            return inputKey;
        }
    }
}