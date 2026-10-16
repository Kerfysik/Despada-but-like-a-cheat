// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Despada.Core;

namespace Despada.Modules.Net;

/// <summary>A draft kept under a name, in the feature's settings ("saved").</summary>
internal sealed class SavedPacket
{
    public string  Name = "";
    public string  TypeName = "";
    public NetDir  Dir;
    public string  Note = "";
    public DateTime? LastSent;
    /// <summary>Field path → value, as the draft editor had them.</summary>
    public Dictionary<string, string> Values = new();
    /// <summary>The object the draft came from, as text, so the rest of its fields come back too; and its BUI wrap.</summary>
    public ObjectSnapshot.Node? Source, Wrap;
    /// <summary>Saved without its snapshot: it was cut or over <see cref="SnapshotCap"/>.</summary>
    public bool NoSnapshot;
    public const int SnapshotCap = 256 * 1024;

    private PacketDraft? _draft;

    /// <summary>
    /// The draft, rebuilt when first needed: from the snapshot when there is one (the saved values
    /// then sit on top as edits), else from the type alone (null until the types are read).
    /// </summary>
    public PacketDraft? Draft()
    {
        if (_draft is not null) return _draft;
        PacketDraft? d = null;
        if (Source is not null)
        {
            try
            {
                var obj = ObjectSnapshot.Materialize(Source, NetTypes.Resolve(TypeName));
                if (obj is not null) { d = PacketDraft.Of(TypeName, obj, Dir); d.ApplyEdits(Values); }
            }
            catch (Exception ex) { Log.Warn("net", $"saved {Name}: snapshot: {ex.Message}"); }
        }
        if (d is null)
        {
            d = PacketDraft.ForType(TypeName);
            if (d is null) return null;
            d.Dir = Dir;
            d.Apply(Values);
        }
        d.Wrap = Wrap;
        return _draft = d;
    }

    public static SavedPacket FromDraft(PacketDraft d, string name)
    {
        var s = new SavedPacket { Name = name, TypeName = d.TypeName, Dir = d.Dir, Source = d.Source, Wrap = d.Wrap, _draft = d.Clone() };
        // What is saved is what would be sent: the built object, edits and all (list items included).
        try
        {
            var built = DraftBuilder.Build(d, out var isWrap);
            if (isWrap)
            {
                s.Wrap   = ObjectSnapshot.Take(built);
                s.Source = NetDecode.Read(built, "Message") is { } inner ? ObjectSnapshot.Take(inner) : s.Source;
            }
            else s.Source = ObjectSnapshot.Take(built);
        }
        catch (Exception ex) { Log.Warn("net", $"save {name}: kept as edits over the capture: {ex.Message}"); }
        if (s.Source is { } src && (!src.Complete || s.Wrap is { Complete: false } || ObjectSnapshot.ToJson(src).Length > SnapshotCap))
        {
            s.Source = s.Wrap = null;
            s.NoSnapshot = true;
        }
        foreach (var (path, f) in d.Walk()) s.Values[path] = f.Value;
        // What the note shows: the fields that aren't at their defaults, the first few.
        s.Note = string.Join(" · ", d.Walk().Where(x => x.Field.Value is not ("" or "0" or "false")).Take(3)
            .Select(x => $"{x.Path} {x.Field.Value}"));
        return s;
    }

    /// <summary>The plain record written to the config.</summary>
    public sealed class Dto
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public string Dir { get; set; } = "out";
        public string Note { get; set; } = "";
        public DateTime? LastSent { get; set; }
        public Dictionary<string, string> Values { get; set; } = new();
        public ObjectSnapshot.Node? Source { get; set; }
        public ObjectSnapshot.Node? Wrap { get; set; }
    }

    public Dto ToDto() => new()
    {
        Name = Name, Type = TypeName, Dir = Dir == NetDir.In ? "in" : "out", Note = Note, LastSent = LastSent, Values = Values,
        Source = Source, Wrap = Wrap,
    };

    public static SavedPacket FromDto(Dto d) => new()
    {
        Name = d.Name, TypeName = d.Type, Dir = d.Dir == "in" ? NetDir.In : NetDir.Out, Note = d.Note, LastSent = d.LastSent,
        Values = d.Values, Source = d.Source, Wrap = d.Wrap,
    };
}
