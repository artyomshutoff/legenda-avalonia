using System.Linq;
using System.Text.RegularExpressions;

namespace Legenda.App;

public static class PhoneNumber
{
    public static bool TryNormalize(string? input, out string formatted)
    {
        formatted = "";
        var text = (input ?? "").Trim();
        if (text.Length > 40 || !Regex.IsMatch(text,
            @"\A(?:\+7|8) ?(?:\([0-9]{3}\)|[0-9]{3}) ?[0-9]{3}[ -]?[0-9]{2}[ -]?[0-9]{2}\z"))
            return false;
        var digits = new string(text.Where(c => c >= '0' && c <= '9').ToArray());
        formatted = $"+7 ({digits.Substring(1, 3)}) {digits.Substring(4, 3)}-{digits.Substring(7, 2)}-{digits.Substring(9, 2)}";
        return true;
    }
}
