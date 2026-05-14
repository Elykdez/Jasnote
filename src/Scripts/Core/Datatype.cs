namespace Jesnote.Core;

public enum SearchType
{
    Key,
    String,
    Number,
    Keyword,
}

public readonly record struct ProgressInfo(
    int CurrentStep,
    int TotalSteps,
    int Size,
    double Progress
);
