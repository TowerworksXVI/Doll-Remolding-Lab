namespace Remold.App.ViewModels;

/// <summary>The three steps of the main window. The step is the identity; its caption is only what the
/// step list shows.</summary>
public enum WorkflowStep
{
    Pick,
    Edit,
    Build,
}

/// <summary>One row of the step list: the step and the caption shown for it.</summary>
public sealed record WorkflowStepItem(WorkflowStep Step, string Caption)
{
    public override string ToString() => Caption;
}
