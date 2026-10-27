using MicTap.Core;

namespace MicTap.App;

/// <summary>
/// A model's line in the spending table; <paramref name="Bar"/> is the width of its rubles bar. The numbers sort the
/// table, the texts show them.
/// </summary>
public sealed record SpendRow(string Model, double Rubles, double Minutes, int Requests, long Words, long InputTokens, long OutputTokens, double Bar)
{
    public string RublesText => $"{Rubles:0.00} ₽";

    public string MinutesText => $"{Minutes:0.0}";

    public long Tokens => InputTokens + OutputTokens;

    public string TokensText => Tokens > 0 ? $"{InputTokens} / {OutputTokens}" : "—";
}

/// <summary>What a session spent in Router AI, model by model, biggest spender first, as the spending dialog shows it.</summary>
public sealed class SessionSpendView(string session, IReadOnlyList<SpendRow> rows, string total, string note)
{
    private const double BarWidth = 150;

    public string Session { get; } = session;

    public IReadOnlyList<SpendRow> Rows { get; } = rows;

    public string Total { get; } = total;

    /// <summary>What cannot be told apart by model: spent before cloud.json counted models.</summary>
    public string Note { get; } = note;

    public bool HasRows => Rows.Count > 0;

    public static SessionSpendView For(SessionInfo session, CloudUsage usage)
    {
        double max = usage.ByModel.Values.Select(m => m.Rubles).DefaultIfEmpty(0).Max();
        SpendRow[] rows =
        [
            .. usage.ByModel
                .OrderByDescending(m => m.Value.Rubles)
                .Select(m => new SpendRow(
                    m.Key,
                    m.Value.Rubles,
                    m.Value.Seconds / 60,
                    m.Value.Requests,
                    m.Value.Words,
                    m.Value.InputTokens,
                    m.Value.OutputTokens,
                    max > 0 ? Math.Max(2, BarWidth * m.Value.Rubles / max) : 2)),
        ];
        string note = usage.RublesWithoutModel >= 0.005
            ? $"Ещё {usage.RublesWithoutModel:0.00} ₽ записаны до того, как mictap начал считать по моделям, — без разбивки."
            : "";
        string total = usage.Requests == 0
            ? "Эта сессия Router AI не пользовалась."
            : $"Всего {usage.Rubles:0.00} ₽: {usage.Requests} запросов, {usage.Seconds / 60:0.0} мин звука (у сравнения — по каждой модели отдельно).";
        return new SessionSpendView(session.Name, rows, total, note);
    }
}
