using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

namespace T6_Workbench
{
    #region LooseMapEnts
    /// <summary>
    /// LooseMapEntsValidation. Parse entity text and preserve base entities during additive edits.
    /// </summary>
    public struct LooseMapEntsValidation { public int OriginalEntityCount { get; set; } public int EditedEntityCount { get; set; } public bool OriginalEntitiesPreserved { get; set; } public LooseMapEntsValidation(int OriginalEntityCount, int EditedEntityCount, bool OriginalEntitiesPreserved) { this.OriginalEntityCount = OriginalEntityCount; this.EditedEntityCount = EditedEntityCount; this.OriginalEntitiesPreserved = OriginalEntitiesPreserved; } }

    /// <summary>
    /// MapEntsField. Parse entity text and preserve base entities during additive edits.
    /// </summary>
    public struct MapEntsField { public string Key { get; set; } public string Value { get; set; } public MapEntsField(string Key, string Value) { this.Key = Key; this.Value = Value; } }
    /// <summary>
    /// MapEntsEntry. Parse entity text and preserve base entities during additive edits.
    /// </summary>
    public sealed class MapEntsEntry { public IReadOnlyList<MapEntsField> Fields { get; set; } public MapEntsEntry(IReadOnlyList<MapEntsField> Fields) { this.Fields = Fields; } }

    /// <summary>Parser and additive-edit validation for quoted spawn-var strings used by loose T6 MapEnts.</summary>
    public static class LooseMapEnts
    {
        /// <summary>
        /// Performs the  validate operation. Parse entity text and preserve base entities during additive edits.
        /// </summary>
        public static void Validate(string text) => _ = ParseEntities(text);

        /// <summary>
        /// Read container metadata without claiming arbitrary asset edit support.
        /// </summary>
        public static IReadOnlyList<MapEntsEntry> Inspect(string text) => ParseEntities(text)
            .Select(entity => new MapEntsEntry(entity.Select(pair => new MapEntsField(pair.Key, pair.Value)).ToArray()))
            .ToArray();

        /// <summary>
        /// Validates both entity strings and requires every original entity's ordered key/value pairs
        /// to remain unchanged and in place. Whitespace and line-ending changes are allowed.
        /// </summary>
        public static LooseMapEntsValidation ValidateAdditive(string originalText, string editedText)
        {
            var original = ParseEntities(originalText);
            var edited = ParseEntities(editedText);
            if (edited.Count < original.Count)
                throw BaseEntitiesChanged();
            for (int i = 0; i < original.Count; i++)
                if (!original[i].SequenceEqual(edited[i]))
                    throw BaseEntitiesChanged();
            return new LooseMapEntsValidation(original.Count, edited.Count, true);
        }

        static InvalidDataException BaseEntitiesChanged() =>
            new System.IO.InvalidDataException(EnglishText.Text("A MapEnts edit must preserve all original entities and their key/value pairs unchanged and in the original order. New entities can only be added after the originals; formatting and line endings may be changed."));

        /// <summary>
        /// Tokenize brace-delimited entity key/value text and reject malformed input.
        /// </summary>
        static List<List<EntityPair>> ParseEntities(string text)
        {
            if (text.Length == 0 || Encoding.UTF8.GetByteCount(text) > 64 * 1024 * 1024)
                throw new InvalidDataException(EnglishText.Text("The .mapents entity string is empty or exceeds the 64 MiB limit."));
            if (text.Contains('\0'))
                throw new InvalidDataException(EnglishText.Text("Entity string .mapents contains a null byte."));

            int cursor = 0;
            bool hasWorldspawn = false;
            var entities = new List<List<EntityPair>>();
            while (SkipWhitespace(text, ref cursor))
            {
                if (text[cursor++] != '{')
                    throw Error(cursor - 1, EnglishText.Text("expected opening brace '{'"));
                var pairs = new List<EntityPair>();
                string classname = null;
                while (true)
                {
                    SkipWhitespace(text, ref cursor);
                    if (cursor >= text.Length)
                        throw Error(cursor, EnglishText.Text("missing closing brace '}'"));
                    if (text[cursor] == '}')
                    {
                        cursor++;
                        break;
                    }
                    string key = ReadQuoted(text, ref cursor);
                    SkipWhitespace(text, ref cursor);
                    string value = ReadQuoted(text, ref cursor);
                    pairs.Add(new LooseMapEnts.EntityPair(key, value));
                    if (key.Equals("classname", StringComparison.OrdinalIgnoreCase))
                        classname = value;
                }
                if (pairs.Count == 0)
                    throw Error(cursor - 1, EnglishText.Text("entity contains no key/value pairs"));
                if (classname?.Equals("worldspawn", StringComparison.OrdinalIgnoreCase) == true)
                    hasWorldspawn = true;
                entities.Add(pairs);
            }

            if (entities.Count == 0 || !hasWorldspawn)
                throw new InvalidDataException(EnglishText.Text("The .mapents entity string must contain at least one worldspawn entity."));
            return entities;
        }

        /// <summary>
        /// Resolves or reads the Quoted value. Parse entity text and preserve base entities during additive edits.
        /// </summary>
        static string ReadQuoted(string text, ref int cursor)
        {
            if (cursor >= text.Length || text[cursor++] != '"')
                throw Error(cursor - 1, EnglishText.Text("expected a quoted string"));
            var value = new StringBuilder();
            while (cursor < text.Length)
            {
                char ch = text[cursor++];
                if (ch == '"')
                    return value.ToString();
                if (ch == '\\')
                {
                    if (cursor >= text.Length)
                        throw Error(cursor, EnglishText.Text("unfinished escape sequence"));
                    value.Append(text[cursor++]);
                }
                else
                    value.Append(ch);
            }
            throw Error(cursor, EnglishText.Text("unterminated string"));
        }

        static bool SkipWhitespace(string text, ref int cursor)
        {
            while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
                cursor++;
            return cursor < text.Length;
        }

        static InvalidDataException Error(int position, string message) =>
            new System.IO.InvalidDataException(EnglishText.Format("Invalid .mapents at position {0}: {1}.", position, message));

        /// <summary>
        /// EntityPair. Parse entity text and preserve base entities during additive edits.
        /// </summary>
        struct EntityPair { public string Key { get; set; } public string Value { get; set; } public EntityPair(string Key, string Value) { this.Key = Key; this.Value = Value; } }
    }
    #endregion

    #region MapEntsSceneDocument
    /// <summary>
    /// MapEntsSceneDocument. UI-independent editable entity document and serializer.
    /// </summary>
    public sealed class MapEntsSceneDocument
    {
        readonly string original;
        readonly List<MapEntsSceneEntity> entities;
        public int OriginalCount
        {
            get;
        }
        public IReadOnlyList<MapEntsSceneEntity> Entities => entities;

        /// <summary>
        /// Initializes MapEntsSceneDocument and owned state. Dependencies are supplied by the caller or application layout.
        /// </summary>
        public MapEntsSceneDocument(string originalText, string editedText = null)
        {
            original = originalText;
            string source = editedText ?? originalText;
            OriginalCount = LooseMapEnts.ValidateAdditive(originalText, source).OriginalEntityCount;
            entities = LooseMapEnts.Inspect(source).Select((entry, index) =>
                new MapEntsSceneEntity(index < OriginalCount, entry.Fields.ToList())).ToList();
        }

        /// <summary>
        /// Add a script_model entity with a generated unique name.
        /// </summary>
        public MapEntsSceneEntity AddModel(string model, Vector3 origin)
        {
            var entity = new MapEntsSceneEntity(false,
            new List<MapEntsField>(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(new MapEntsField[] { new MapEntsField("classname", "script_model") }, new MapEntsField[] { new MapEntsField("model", model) })), new MapEntsField[] { new MapEntsField("origin", MapEntsSceneEntity.Format(origin)) })), new MapEntsField[] { new MapEntsField("angles", "0 0 0") })), new MapEntsField[] { new MapEntsField("targetname", "workbench_model_" + Guid.NewGuid().ToString("N").Substring(0, (10) - (0))) }))));
            entity.SetModel(model);
            entities.Add(entity);
            return entity;
        }

        /// <summary>
        /// Delete an added entity while protecting original entities.
        /// </summary>
        public void Remove(MapEntsSceneEntity entity)
        {
            if (entity.IsOriginal)
                throw new InvalidOperationException(EnglishText.Text("Original entity cannot be removed."));
            if (!entities.Remove(entity))
                throw new ArgumentException(EnglishText.Text("Entity does not belong to the document."), nameof(entity));
        }

        /// <summary>
        /// Append custom entities to preserved base text and validate the serialized result.
        /// </summary>
        public string Render()
        {
            var result = new StringBuilder(original.TrimEnd());
            foreach (var entity in entities.Where(item => !item.IsOriginal))
            {
                result.AppendLine().AppendLine("{");
                foreach (var field in entity.Fields)
                    result.Append('"').Append(Escape(field.Key)).Append("\" \"")
                        .Append(Escape(field.Value)).AppendLine("\"");
                result.AppendLine("}");
            }
            result.AppendLine();
            string text = result.ToString();
            LooseMapEnts.ValidateAdditive(original, text);
            return text;
        }

        static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    /// <summary>
    /// MapEntsSceneEntity. UI-independent editable entity document and serializer.
    /// </summary>
    public sealed class MapEntsSceneEntity
    {
        readonly List<MapEntsField> fields;
        public bool IsOriginal
        {
            get;
        }
        public IReadOnlyList<MapEntsField> Fields => fields;
        public string ClassName => Get("classname");
        public string Model => Get("model");
        public string Label => string.IsNullOrWhiteSpace(Get("targetname")) ? Model : Get("targetname");
        public Vector3? Origin => ParseVector(Get("origin"));
        public Vector3 Angles => ParseVector(Get("angles")) ?? new Vector3(0,
            float.TryParse(Get("angle"), NumberStyles.Float, CultureInfo.InvariantCulture, out var yaw) ? yaw : 0, 0);

        /// <summary>
        /// Initializes MapEntsSceneEntity and owned state. Dependencies are supplied by the caller or application layout.
        /// </summary>
        internal MapEntsSceneEntity(bool isOriginal, List<MapEntsField> fields)
        {
            IsOriginal = isOriginal;
            this.fields = fields;
        }

        /// <summary>
        /// Set finite position and angles on an added entity.
        /// </summary>
        public void SetPlacement(Vector3 origin, Vector3 angles)
        {
            if (IsOriginal)
                throw new InvalidOperationException(EnglishText.Text("Original entity is read only."));
            if (!Finite(origin) || !Finite(angles))
                throw new ArgumentException(EnglishText.Text("Coordinates and angles must be finite numbers."));
            Set("origin", Format(origin));
            Set("angles", Format(angles));
            fields.RemoveAll(field => field.Key.Equals("angle", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Updates, serializes or converts the Model value. UI-independent editable entity document and serializer.
        /// </summary>
        public void SetModel(string model)
        {
            if (IsOriginal)
                throw new InvalidOperationException(EnglishText.Text("Original entity is read only."));
            if (string.IsNullOrWhiteSpace(model) || model.Contains('"') || model.Contains('\\'))
                throw new ArgumentException(EnglishText.Text("Enter a valid xmodel name."), nameof(model));
            Set("model", model);
        }

        public string Get(string key) => fields.FirstOrDefault(field =>
            field.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value ?? "";

        /// <summary>
        /// Updates, serializes or converts the  value. UI-independent editable entity document and serializer.
        /// </summary>
        void Set(string key, string value)
        {
            int index = fields.FindIndex(field => field.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                fields.Add(new MapEntsField(key, value));
            else
                fields[index] = new MapEntsField(fields[index].Key, value);
        }

        internal static string Format(Vector3 vector) => string.Join(" ",
            vector.X.ToString("0.###", CultureInfo.InvariantCulture),
            vector.Y.ToString("0.###", CultureInfo.InvariantCulture),
            vector.Z.ToString("0.###", CultureInfo.InvariantCulture));

        /// <summary>
        /// Resolves or reads the Vector value. UI-independent editable entity document and serializer.
        /// </summary>
        static Vector3? ParseVector(string text)
        {
            var values = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (values.Length != 3 || !float.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !float.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
                !float.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                return null;
            var result = new Vector3(x, y, z);
            return Finite(result) ? (Vector3?)result : null;
        }

        static bool Finite(Vector3 vector) => Helpers.IsFinite(vector.X) && Helpers.IsFinite(vector.Y) && Helpers.IsFinite(vector.Z);
    }
    #endregion
}
