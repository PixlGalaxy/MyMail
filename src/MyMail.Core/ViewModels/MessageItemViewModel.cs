using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MyMail.Core.Models;

namespace MyMail.Core.ViewModels;

public sealed partial class MessageItemViewModel : ObservableObject
{
    public MessageItemViewModel(MailMessageSummary summary)
    {
        Summary = summary;
        _isRead = summary.IsRead;
        _isFlagged = summary.IsFlagged;
    }

    public MailMessageSummary Summary { get; }

    public string Id => Summary.Id;

    public string FromName => Summary.From.Name;

    public string FromEmail => Summary.From.Email;

    public string Initials => Summary.From.Initials;

    public string Subject => Summary.Subject;

    public string Preview => Summary.Preview;

    public bool HasAttachments => Summary.HasAttachments;

    /// <summary>Time for today, weekday for this week, short date otherwise (as in the original list).</summary>
    public string ReceivedText
    {
        get
        {
            var local = Summary.Received.ToLocalTime();
            var today = DateTime.Today;
            if (local.Date == today)
            {
                return local.ToString("t", CultureInfo.CurrentCulture);
            }

            return local.Date > today.AddDays(-7)
                ? local.ToString("ddd HH:mm", CultureInfo.CurrentCulture)
                : local.ToString("d", CultureInfo.CurrentCulture);
        }
    }

    public string ReceivedFullText => Summary.Received.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);

    [ObservableProperty]
    private bool _isRead;

    /// <summary>Used by screen readers as the list item name.</summary>
    public override string ToString() =>
        IsRead ? $"{FromName}, {Subject}, {ReceivedText}" : $"No leído, {FromName}, {Subject}, {ReceivedText}";

    [ObservableProperty]
    private bool _isFlagged;
}
