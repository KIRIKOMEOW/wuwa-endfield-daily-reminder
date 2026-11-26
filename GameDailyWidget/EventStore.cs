using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace GameDailyWidget;

internal sealed class EventStore
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private readonly string? _path;
    private bool _readOnly;
    internal EventData Data { get; private set; } = new();
    internal string? Warning { get; private set; }

    // A null path is an entirely in-memory store, including initial load.
    internal EventStore(string? path = null)
    {
        _path = path;
        if (path == null) return;
        try
        {
            using var input = File.OpenRead(path);
            if (input.Length > 16 * 1024 * 1024) throw new InvalidDataException();
            using var json = JsonDocument.Parse(input);
            var root = json.RootElement;
            foreach (var field in new[] { "schema", "events", "sync", "articles", "maintenance", "marks", "sent" })
                if (!root.TryGetProperty(field, out _)) throw new InvalidDataException();
            var data = root.Deserialize<EventData>(Json);
            if (data == null || data.Schema is not (1 or 2) || data.Pools == null ||
                (data.Schema == 2 && !root.TryGetProperty("pools", out _)) ||
                data.Pools.Any(p => !ValidPool(p)) ||
                data.Events == null || data.Marks == null || data.Sent == null
                || data.Sync == null || data.Articles == null || data.Maintenance == null
                || data.Events.Any(e => e?.Source == null || e.Currency == null || e.Game is not ("wuwa" or "endfield")
                    || string.IsNullOrWhiteSpace(e.Name) || string.IsNullOrWhiteSpace(e.Key) || e.Start == default)
                || data.Maintenance.Values.Any(list => list == null || list.Any(m => m == null))
                || data.Sync.Values.Any(s => s == null) || data.Articles.Values.Any(s => s == null || s.ParsedPools?.Any(p => !ValidPool(p, allowEmpty:true)) == true)
                || data.Marks.Values.Any(m => m == null)) throw new InvalidDataException();
            data.Schema = 2;
            Data = data;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch { _readOnly = true; Warning = "活动记录无法读取，已保留原文件 · 仅本次会话有效"; }
    }

    private static bool ValidPool(GamePool? p, bool allowEmpty = false) => p?.Source != null && p.Game is ("wuwa" or "endfield") &&
        !string.IsNullOrWhiteSpace(p.Name) && !string.IsNullOrWhiteSpace(p.Key) && p.Items != null && (allowEmpty || p.Items.Count > 0) &&
        p.Items.All(i => i != null && !string.IsNullOrWhiteSpace(i.Name) && i.Stars is >= 1 and <= 6);

    internal void Save()
    {
        if (_path == null || _readOnly) return;
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, Data, Json); stream.Flush(true); }
            File.Move(temp, _path, true);
            Warning = null;
        }
        catch { Warning = "活动记录保存失败 · 仅本次会话有效"; }
        finally { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    internal void Clean(DateTimeOffset now)
    {
        foreach (var e in Data.Events.Where(e => e.Deadline < now.AddDays(-14)).ToArray())
        {
            Data.Events.Remove(e);
            Data.Marks.Remove(e.Key);
            foreach (var key in Data.Sent.Keys.Where(k => k.StartsWith(e.Key + "|", StringComparison.Ordinal)).ToArray())
                Data.Sent.Remove(key);
        }
    }

    internal void MarkAutomatically(GameEvent item, DateTimeOffset now)
    {
        var mark = Data.Marks.GetValueOrDefault(item.Key);
        if (item.Game != "endfield" || item.Start > now || item.Deadline <= now ||
            mark is { Claimed:true } or { ManualOverride:true } or { Ignored:true }) return;
        Data.Marks[item.Key] = new(true,false,now,Automatic:true);
        Save();
    }

    internal static bool CanClaim(GameEvent item, EventMark? mark, DateTimeOffset now) =>
        item.Start <= now || mark?.Claimed == true;

    internal void Mark(GameEvent item, bool claimed, bool ignored, DateTimeOffset now)
    {
        var previous = Data.Marks.GetValueOrDefault(item.Key);
        if (claimed && !CanClaim(item, previous, now)) return;
        if (!claimed && previous?.Claimed == true)
            foreach (var due in EventDeadlineReminder.Due(item, now)) Data.Sent.TryAdd(due.Key, now);
        Data.Marks[item.Key] = new(claimed, ignored, now, claimed && previous?.Automatic == true,
            previous?.ManualOverride == true || (!claimed && previous?.Automatic == true));
        Save();
    }
}
