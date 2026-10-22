using FocusWindow.Core.Session;
using Xunit;

namespace FocusWindow.Tests;

public sealed class PhoneSpeakerReturnPolicyTests
{
    private const string Phone = "{0.0.0.00000000}.{deskfuse-phone}";
    private const string Laptop = "{0.0.0.00000000}.{laptop-speaker}";
    private const string Headset = "{0.0.0.00000000}.{headset-speaker}";

    [Fact]
    public void Selected_phone_returns_only_to_previously_selected_roles()
    {
        var policy = new PhoneSpeakerReturnPolicy(Phone,
            new AudioRenderDefaults(Phone, Laptop));
        Assert.True(policy.WasSelected);
        policy.RecordAutomaticFallback(new AudioRenderDefaults(Laptop, Laptop));

        var restore = policy.ShouldRestore(Phone, new AudioRenderDefaults(Laptop, Laptop));
        Assert.True(restore.Console);
        Assert.False(restore.Multimedia);
    }

    [Fact]
    public void Phone_not_selected_at_departure_never_overrides_windows()
    {
        var policy = new PhoneSpeakerReturnPolicy(Phone,
            new AudioRenderDefaults(Laptop, Headset));
        Assert.False(policy.WasSelected);
        policy.RecordAutomaticFallback(new AudioRenderDefaults(Laptop, Headset));

        var restore = policy.ShouldRestore(Phone, new AudioRenderDefaults(Laptop, Headset));
        Assert.False(restore.Console);
        Assert.False(restore.Multimedia);
    }

    [Fact]
    public void Explicit_change_while_phone_absent_vetoes_restore_even_if_changed_back()
    {
        var policy = new PhoneSpeakerReturnPolicy(Phone,
            new AudioRenderDefaults(Phone, Phone));
        policy.RecordAutomaticFallback(new AudioRenderDefaults(Laptop, Laptop));
        policy.ObserveAbsent(new AudioRenderDefaults(Headset, Headset));
        policy.ObserveAbsent(new AudioRenderDefaults(Laptop, Laptop));

        var restore = policy.ShouldRestore(Phone, new AudioRenderDefaults(Laptop, Laptop));
        Assert.False(restore.Console);
        Assert.False(restore.Multimedia);
    }

    [Fact]
    public void Windows_natural_reselection_needs_no_policy_write()
    {
        var policy = new PhoneSpeakerReturnPolicy(Phone,
            new AudioRenderDefaults(Phone, Phone));
        policy.RecordAutomaticFallback(new AudioRenderDefaults(Laptop, Laptop));

        var restore = policy.ShouldRestore(Phone, new AudioRenderDefaults(Phone, Phone));
        Assert.False(restore.Console);
        Assert.False(restore.Multimedia);
    }

    [Fact]
    public void Changed_default_before_return_is_not_overridden()
    {
        var policy = new PhoneSpeakerReturnPolicy(Phone,
            new AudioRenderDefaults(Phone, Phone));
        policy.RecordAutomaticFallback(new AudioRenderDefaults(Laptop, Laptop));

        var restore = policy.ShouldRestore(Phone, new AudioRenderDefaults(Headset, Headset));
        Assert.False(restore.Console);
        Assert.False(restore.Multimedia);
    }

    [Fact]
    public void Restoration_requires_actual_post_departure_fallback()
    {
        var policy = new PhoneSpeakerReturnPolicy(Phone,
            new AudioRenderDefaults(Phone, Phone));

        var restore = policy.ShouldRestore(Phone, new AudioRenderDefaults(Laptop, Laptop));
        Assert.False(restore.Console);
        Assert.False(restore.Multimedia);
    }

    [Fact]
    public void Different_windows_roles_can_be_overridden_independently()
    {
        var policy = new PhoneSpeakerReturnPolicy(Phone,
            new AudioRenderDefaults(Phone, Phone));
        policy.RecordAutomaticFallback(new AudioRenderDefaults(Laptop, Laptop));
        policy.ObserveAbsent(new AudioRenderDefaults(Laptop, Headset));

        var restore = policy.ShouldRestore(Phone, new AudioRenderDefaults(Laptop, Headset));
        Assert.True(restore.Console);
        Assert.False(restore.Multimedia);
    }
}
