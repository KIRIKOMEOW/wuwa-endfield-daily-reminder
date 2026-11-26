using System;
using System.Text.Json;

namespace EndfieldDesktopWidget;

internal static class AccountTokenInput
{
    internal static string Parse(string input)
    {
        string token = input.Trim();
        if (token.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(token);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data)
                    && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.String)
                    token = content.GetString() ?? "";
                else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("token", out var directToken)
                    && directToken.ValueKind == JsonValueKind.String)
                    token = directToken.GetString() ?? "";
                else return "";
            }
            catch (JsonException) { return ""; }
        }
        token = token.Trim().Trim('"');
        if (token.Length > 4096) return "";
        foreach (char character in token) if (char.IsControl(character)) return "";
        return token;
    }
}
