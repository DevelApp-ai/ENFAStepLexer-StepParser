using System;
using Xunit;
using DevelApp.StepLexer;

namespace DevelApp.StepParser.Tests
{
    /// <summary>
    /// Tests for stable symbol references and identifier-level span fidelity
    /// (ENFAStepLexer-StepParser issue #95).
    /// </summary>
    public class SymbolRefTests
    {
        private static CodeLocation MakeLocation(string file, int sl, int sc, int el, int ec)
        {
            return new CodeLocation(file, sl, sc, el, ec);
        }

        [Fact]
        public void SameTypeInDifferentNamespaces_ResolvesToDistinctSymbolRefs()
        {
            // Arrange: two files, class A in namespace N1 (file one) and N2 (file two)
            var table = new ScopeAwareSymbolTable { Project = "sample" };

            // Act
            table.Declare("A", "class", "N1", MakeLocation("One.cs", 3, 18, 3, 19), MakeLocation("One.cs", 3, 18, 3, 19));
            table.Declare("A", "class", "N2", MakeLocation("Two.cs", 3, 18, 3, 19), MakeLocation("Two.cs", 3, 18, 3, 19));

            var refN1 = table.Lookup("A", "N1")!.SymbolRef;
            var refN2 = table.Lookup("A", "N2")!.SymbolRef;

            // Assert: cross-file identity is distinct per namespace
            Assert.NotEqual(refN1, refN2);
            Assert.Equal("sample|N1.A", refN1.Id);
            Assert.Equal("sample|N2.A", refN2.Id);
            Assert.Equal("A", refN1.Name);
            Assert.Equal("N1.A", refN1.QualifiedName);
        }

        [Fact]
        public void SameDeclarationSeenFromTwoLookups_ProducesSameSymbolRef()
        {
            var table = new ScopeAwareSymbolTable { Project = "sample" };
            table.Declare("A", "class", "N1", MakeLocation("One.cs", 3, 18, 3, 19));

            var first = table.Lookup("A", "N1")!.SymbolRef;
            var second = table.Lookup("A", "N1.Nested.Method")!.SymbolRef;

            Assert.Equal(first, second);
            Assert.Equal("sample|N1.A", first.Id);
        }

        [Fact]
        public void LookupRef_FindsEntryByStableId()
        {
            var table = new ScopeAwareSymbolTable { Project = "sample" };
            table.Declare("A", "class", "N1", MakeLocation("One.cs", 3, 18, 3, 19));
            var refFromLookup = table.Lookup("A", "N1")!.SymbolRef;

            var entry = table.LookupRef(refFromLookup);

            Assert.NotNull(entry);
            Assert.Equal("One.cs", entry!.Location.File);
            Assert.Equal(refFromLookup, entry.SymbolRef);
        }

        [Fact]
        public void Declare_WithExplicitQualifiedName_UsesItForIdentity()
        {
            var table = new ScopeAwareSymbolTable { Project = "sample" };
            table.Declare("A", "class", "file", MakeLocation("Two.cs", 1, 1, 1, 1), null, "N2.A");

            var entry = table.Lookup("A", "file");

            Assert.NotNull(entry);
            Assert.Equal("sample|N2.A", entry!.SymbolRef.Id);
        }

        [Fact]
        public void IdentifierSpan_IsDistinctFromDeclarationSpan()
        {
            // Arrange: "public class Customer   : Entity" — declaration span
            // covers the whole line, identifier span covers just "Customer"
            var table = new ScopeAwareSymbolTable { Project = "sample" };
            var declarationSpan = MakeLocation("Customer.cs", 5, 14, 5, 38);
            var identifierSpan = MakeLocation("Customer.cs", 5, 14, 5, 22);
            table.Declare("Customer", "class", "Shop", declarationSpan, identifierSpan);

            var entry = table.Lookup("Customer", "Shop")!;

            // Assert: both spans carried, identifier span distinct from declaration
            Assert.NotNull(entry.IdentifierSpan);
            Assert.Equal(5, entry.IdentifierSpan!.StartLine);
            Assert.Equal(14, entry.IdentifierSpan.StartColumn);
            Assert.Equal(5, entry.IdentifierSpan.EndLine);
            Assert.Equal(22, entry.IdentifierSpan.EndColumn);
            Assert.True(entry.IdentifierSpan.StartColumn > entry.Location.StartColumn
                        || entry.IdentifierSpan.EndColumn < entry.Location.EndColumn);
        }

        [Fact]
        public void Project_DefaultsAndIdentityStability()
        {
            var table = new ScopeAwareSymbolTable();
            Assert.Equal("project", table.Project);

            table.Declare("x", "int", "global", MakeLocation("a.cs", 1, 1, 1, 5));
            var before = table.Lookup("x", "global")!.SymbolRef;

            // Re-declaration of the same symbol identity keeps the same ref id
            table.Declare("x", "int", "global", MakeLocation("a.cs", 1, 1, 1, 5));
            var after = table.Lookup("x", "global")!.SymbolRef;

            Assert.Equal(before, after);
        }

        [Fact]
        public void SymbolRef_ConstructorsAndEquality()
        {
            var a = new SymbolRef("p", "N.A");
            var b = new SymbolRef("p", "N.A", "A");
            var c = new SymbolRef("p", "N.B");

            Assert.Equal(a, b);
            Assert.NotEqual(a, c);
            Assert.Equal("A", a.Name);
            Assert.Equal("p|N.A", a.ToString());
            Assert.False(a.IsEmpty);

            Assert.Throws<ArgumentException>(() => new SymbolRef("", "N.A"));
            Assert.Throws<ArgumentException>(() => new SymbolRef("p", ""));
        }
    }
}
