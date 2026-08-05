using System.Windows;
using System.Windows.Automation;

namespace WriteLite.Services;

public sealed class ActiveTextTargetManager
{
    private ActiveTextTargetIdentity? _currentIdentity;

    public AutomationTextTarget? CurrentTarget { get; private set; }
    public int GenerationId { get; private set; }

    public bool Update(AutomationElement element, AutomationTextTarget target)
    {
        var identity = ActiveTextTargetIdentity.From(element);
        if (_currentIdentity == identity)
        {
            CurrentTarget = target;
            return false;
        }

        _currentIdentity = identity;
        CurrentTarget = target;
        GenerationId++;
        return true;
    }

    public bool UpdateIdentityForTest(ActiveTextTargetIdentity identity)
    {
        if (_currentIdentity == identity)
        {
            return false;
        }

        _currentIdentity = identity;
        CurrentTarget = null;
        GenerationId++;
        return true;
    }

    public bool BelongsToCurrent(int generationId, AutomationTextTarget target)
    {
        return generationId == GenerationId &&
               CurrentTarget is not null &&
               CurrentTarget.Identity == target.Identity;
    }

    public void Clear()
    {
        _currentIdentity = null;
        CurrentTarget = null;
        GenerationId++;
    }
}

public sealed record ActiveTextTargetIdentity(
    int ProcessId,
    int NativeWindowHandle,
    string RuntimeId,
    string ControlType,
    string AutomationId,
    string FrameworkId)
{
    public static ActiveTextTargetIdentity From(AutomationElement element)
    {
        int[] runtimeId;
        try
        {
            runtimeId = element.GetRuntimeId();
        }
        catch
        {
            runtimeId = [];
        }

        return new ActiveTextTargetIdentity(
            element.Current.ProcessId,
            element.Current.NativeWindowHandle,
            string.Join(".", runtimeId),
            element.Current.ControlType.ProgrammaticName,
            element.Current.AutomationId ?? string.Empty,
            element.Current.FrameworkId ?? string.Empty);
    }
}
