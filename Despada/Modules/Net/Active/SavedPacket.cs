// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

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

    private PacketDraft? _draft;

    /// <summary>The draft, rebuilt from the type when first needed (null until the types are read).</summary>
    public PacketDraft? Draft()
    {
        if (_draft is not null) return _draft;
        var d = PacketDraft.ForType(TypeName);
        if (d is null) return null;
        d.Dir = Dir;
        d.Apply(Values);
        return _draft = d;
    }

    public static SavedPacket FromDraft(PacketDraft d, string name)
    {
        var s = new SavedPacket { Name = name, TypeName = d.TypeName, Dir = d.Dir, _draft = d.Clone() };
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
    }

    public Dto ToDto() => new()
    {
        Name = Name, Type = TypeName, Dir = Dir == NetDir.In ? "in" : "out", Note = Note, LastSent = LastSent, Values = Values,
    };

    public static SavedPacket FromDto(Dto d) => new()
    {
        Name = d.Name, TypeName = d.Type, Dir = d.Dir == "in" ? NetDir.In : NetDir.Out, Note = d.Note, LastSent = d.LastSent,
        Values = d.Values,
    };
}
