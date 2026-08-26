namespace RoboCapture.Core;

public sealed record SubjectRecord(
    string StudentId,
    string FirstName,
    string LastName,
    string Grade,
    string Homeroom,
    string Barcode,
    string Team,
    IReadOnlyDictionary<string, string> CustomFields);

public enum SubjectScanType { Barcode, QrCode }

public sealed record SubjectScanResult(SubjectScanType Type, string Value, SubjectRecord? Subject, string? Error = null)
{
    public bool Found => Subject is not null && Error is null;
}

public sealed class SubjectIdentifier(IReadOnlyCollection<SubjectRecord> roster)
{
    public SubjectScanResult Resolve(string value, SubjectScanType type = SubjectScanType.QrCode)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
            return new(type, normalized, null, "Scan value is empty.");
        var subject = roster.FirstOrDefault(candidate =>
            string.Equals(candidate.Barcode, normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.StudentId, normalized, StringComparison.OrdinalIgnoreCase));
        return subject is null
            ? new(type, normalized, null, "No matching subject.")
            : new(type, normalized, subject);
    }
}

public static class CsvRosterImporter
{
    public static IReadOnlyList<SubjectRecord> Parse(string csv)
    {
        using var reader = new StringReader(csv);
        var headerLine = reader.ReadLine() ?? throw new FormatException("Roster CSV has no header row.");
        var headers = ParseLine(headerLine);
        if (headers.Count == 0 || headers.Any(string.IsNullOrWhiteSpace))
            throw new FormatException("Roster CSV contains an invalid header row.");

        var standard = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "StudentID", "FirstName", "LastName", "Grade", "Homeroom", "Barcode", "Team" };
        var subjects = new List<SubjectRecord>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var values = ParseLine(line);
            if (values.Count != headers.Count)
                throw new FormatException($"Roster row has {values.Count} values; expected {headers.Count}.");
            var fields = headers.Select((header, index) => (header, value: values[index].Trim()))
                .ToDictionary(item => item.header, item => item.value, StringComparer.OrdinalIgnoreCase);
            if (!fields.TryGetValue("StudentID", out var studentId) || string.IsNullOrWhiteSpace(studentId))
                throw new FormatException("Roster row is missing StudentID.");
            subjects.Add(new SubjectRecord(studentId, Value(fields, "FirstName"), Value(fields, "LastName"),
                Value(fields, "Grade"), Value(fields, "Homeroom"), Value(fields, "Barcode"), Value(fields, "Team"),
                fields.Where(field => !standard.Contains(field.Key)).ToDictionary(field => field.Key, field => field.Value)));
        }
        return subjects;
    }

    private static string Value(IReadOnlyDictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var value) ? value : string.Empty;

    private static List<string> ParseLine(string line)
    {
        var values = new List<string>();
        var value = new System.Text.StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"') { value.Append('"'); index++; }
                else quoted = !quoted;
            }
            else if (character == ',' && !quoted) { values.Add(value.ToString()); value.Clear(); }
            else value.Append(character);
        }
        if (quoted) throw new FormatException("Roster CSV contains an unterminated quoted value.");
        values.Add(value.ToString());
        return values;
    }
}