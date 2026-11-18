using System;

namespace DesignMode.AI.Shared.Security
{
    public static class PhoneNumberValidator
    {
        public static bool TryNormalize(string? value, out string? normalized, out string error)
        {
            normalized = null;
            error = string.Empty;
            var candidate = value?.Trim();
            if (string.IsNullOrEmpty(candidate)) return true;

            var digits = 0;
            var plusCount = 0;
            for (var i = 0; i < candidate.Length; i++)
            {
                var c = candidate[i];
                if (c >= '0' && c <= '9') digits++;
                else if (c == '+' && i == 0 && plusCount++ == 0) { }
                else if (c != ' ' && c != '(' && c != ')' && c != '-' && c != '.')
                {
                    error = "Phone number may contain only digits, spaces, parentheses, hyphens, periods, and one leading +.";
                    return false;
                }
            }

            if (candidate.Length < 3 || candidate.Length > 40 || digits < 3 || digits > 15)
            {
                error = "Phone number must be 3-40 characters and contain 3-15 digits.";
                return false;
            }

            normalized = candidate;
            return true;
        }
    }
}
