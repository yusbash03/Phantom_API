using PHANTOM.Domain.Notifications;

namespace PHANTOM.UnitTests.Domain;

public sealed class NotificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Requires_a_recipient_and_a_title()
    {
        Assert.Throws<ArgumentException>(() => Notification.Create(Guid.NewGuid(), Guid.Empty, NotificationType.AssignmentCreated, "t", "b", null, null, Now));
        Assert.Throws<ArgumentException>(() => Notification.Create(Guid.NewGuid(), Guid.NewGuid(), NotificationType.AssignmentCreated, " ", "b", null, null, Now));
    }

    [Fact]
    public void Long_text_is_shortened_with_an_ellipsis()
    {
        var n = Notification.Create(Guid.NewGuid(), Guid.NewGuid(), NotificationType.PossibleDuplicate, new string('t', 300), new string('b', 2000), null, null, Now);
        Assert.Equal(Notification.MaxTitleLength, n.Title.Length);
        Assert.EndsWith("…", n.Title, StringComparison.Ordinal);
        Assert.Equal(Notification.MaxBodyLength, n.Body.Length);
    }

    [Fact]
    public void Marking_read_keeps_the_first_read_time()
    {
        var n = Notification.Create(Guid.NewGuid(), Guid.NewGuid(), NotificationType.AssignmentCreated, "t", "b", null, null, Now);
        Assert.False(n.IsRead);
        n.MarkRead(Now.AddMinutes(1));
        n.MarkRead(Now.AddMinutes(5));
        Assert.Equal(Now.AddMinutes(1), n.ReadAt);
    }
}
