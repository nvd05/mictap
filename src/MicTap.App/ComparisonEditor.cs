using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MicTap.Core;

namespace MicTap.App;

/// <summary>An engine to tick in the comparison dialog; <paramref name="model"/> null is the local Whisper.</summary>
public sealed partial class CompareChoice(string? model, string title, string price, double? rublesPerMinute, bool isChecked, bool isLiked) : ObservableObject
{
    [ObservableProperty]
    private bool _isChecked = isChecked;

    public string? Model { get; } = model;

    public string Title { get; } = title;

    public string Price { get; } = price;

    public double? RublesPerMinute { get; } = rublesPerMinute;

    /// <summary>Liked for the project in the engine list.</summary>
    public bool IsLiked { get; } = isLiked;
}

/// <summary>The comparison dialog: which engines hear the session's audio, and roughly what the paid ones cost.</summary>
public sealed partial class ComparisonEditor : ObservableObject
{
    /// <summary>
    /// Picked at the start: the graphics card, these models the benchmarks and the user rate highest, the project's model
    /// and the models liked for the project (those go on top).
    /// </summary>
    private static readonly string[] Suggested =
    [
        "x-ai/grok-stt-1.0",
        "elevenlabs/scribe-v2",
        "assemblyai/universal-3-6-pro",
        "openai/gpt-transcribe",
    ];

    private readonly double _minutes;

    public ComparisonEditor(SessionInfo session, double minutes, IReadOnlyList<CloudModelInfo> models, ProjectSettings project)
    {
        Session = session;
        _minutes = minutes;
        List<CompareChoice> choices = [ new CompareChoice(null, "Видеокарта · Whisper", "бесплатно", 0, isChecked: true, project.Likes(null)) ];
        foreach (CloudModelInfo model in models)
        {
            bool liked = project.Likes(model.Id);
            choices.Add(new CompareChoice(
                model.Id, model.Id, model.RublesPerMinute is { } price ? $"{price:0.###} ₽/мин" : "по токенам", model.RublesPerMinute,
                liked || Suggested.Contains(model.Id) || model.Id == project.CloudModelInUse, liked));
        }

        foreach (CompareChoice choice in choices.Where(c => c.IsLiked).Concat(choices.Where(c => !c.IsLiked)))
        {
            choice.PropertyChanged += OnChoiceChanged;
            Choices.Add(choice);
        }
    }

    public SessionInfo Session { get; }

    public ObservableCollection<CompareChoice> Choices { get; } = [];

    public IReadOnlyList<CompareChoice> Picked => [ .. Choices.Where(c => c.IsChecked) ];

    public bool CanCompare => Picked.Count >= 2;

    /// <summary>The audio's length and the estimated price of the ticked paid models.</summary>
    public string EstimateText
    {
        get
        {
            double rubles = Picked.Sum(c => (c.RublesPerMinute ?? 0) * _minutes);
            string tokens = Picked.Any(c => c.RublesPerMinute is null) ? " плюс модели по токенам" : "";
            return $"Звука {_minutes:0.#} мин. Выбрано: {Picked.Count}. Примерно {rubles:0.00} ₽{tokens}. "
                + "Основной текст сессии не меняется, результат — в папке compare сессии.";
        }
    }

    private void OnChoiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(EstimateText));
        OnPropertyChanged(nameof(CanCompare));
    }
}
