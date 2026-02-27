using System.Text.RegularExpressions;

namespace TicketKit;

public static partial class TicketIdValidator
{
    public static bool IsValid(string ticketId)
    {
        if (string.IsNullOrWhiteSpace(ticketId))
        {
            return false;
        }

        return TicketIdRegex().IsMatch(ticketId.Trim());
    }

    [GeneratedRegex("^T\\d{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex TicketIdRegex();
}
