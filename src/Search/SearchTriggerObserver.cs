namespace CombatSolver;

/// <summary>Read-only notifications for B2/B3 search gates.</summary>
public interface ISearchTriggerObserver
{
    void Triggered(string gate);
    void Entered(string gate);
}

internal static class SearchTriggers
{
    private static readonly AsyncLocal<ISearchTriggerObserver?> Current = new();

    internal static ISearchTriggerObserver? Observer
    {
        get => Current.Value;
        set => Current.Value = value;
    }

    internal static void Report(string gate) => Observer?.Triggered(gate);
    internal static void Enter(string gate) => Observer?.Entered(gate);
}
