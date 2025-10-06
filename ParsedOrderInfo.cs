using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace botStarsSaller
{
    public class ParsedOrderInfo
    {
        public int StarsCount { get; set; }
        public int Quantity { get; set; } = 1;  // по умолчанию 1
        public string Username { get; set; } = string.Empty;
    }
}
