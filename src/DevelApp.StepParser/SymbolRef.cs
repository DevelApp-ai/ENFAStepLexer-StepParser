using System;

namespace DevelApp.StepParser
{
    /// <summary>
    /// A stable, project-scoped symbol identity for cross-file reference
    /// resolution (ENFAStepLexer-StepParser issue #95).
    /// Two declarations of the same-named type in different namespaces
    /// produce distinct SymbolRefs; the same declaration seen from two files
    /// produces the same SymbolRef.
    /// </summary>
    public readonly struct SymbolRef : IEquatable<SymbolRef>
    {
        /// <summary>
        /// The stable identifier: project|qualified name. Deterministic from
        /// declaration identity, not from parse order or node offsets.
        /// </summary>
        public string Id { get; }

        /// <summary>
        /// The raw (unqualified) declared name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// The resolved qualified name (container path + name), when known.
        /// </summary>
        public string? QualifiedName { get; }

        /// <summary>
        /// Initializes a new instance of the SymbolRef struct.
        /// </summary>
        /// <param name="project">The project scope (e.g. assembly or analysis session name).</param>
        /// <param name="qualifiedName">The fully qualified symbol name.</param>
        /// <param name="name">The raw declared name; defaults to the last segment of the qualified name.</param>
        public SymbolRef(string project, string qualifiedName, string? name = null)
        {
            if (string.IsNullOrEmpty(project)) throw new ArgumentException("Project must not be empty", nameof(project));
            if (string.IsNullOrEmpty(qualifiedName)) throw new ArgumentException("Qualified name must not be empty", nameof(qualifiedName));

            QualifiedName = qualifiedName;
            Name = string.IsNullOrEmpty(name)
                ? qualifiedName.Substring(qualifiedName.LastIndexOf('.') + 1)
                : name;
            Id = $"{project}|{qualifiedName}";
        }

        /// <summary>
        /// Whether this reference has been assigned an identity.
        /// </summary>
        public bool IsEmpty => string.IsNullOrEmpty(Id);

        /// <inheritdoc />
        public bool Equals(SymbolRef other) => string.Equals(Id, other.Id, StringComparison.Ordinal);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is SymbolRef other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => Id.GetHashCode(StringComparison.Ordinal);

        /// <inheritdoc />
        public override string ToString() => Id;

        /// <summary>
        /// Compares two SymbolRefs for equality.
        /// </summary>
        public static bool operator ==(SymbolRef left, SymbolRef right) => left.Equals(right);

        /// <summary>
        /// Compares two SymbolRefs for inequality.
        /// </summary>
        public static bool operator !=(SymbolRef left, SymbolRef right) => !left.Equals(right);
    }
}
