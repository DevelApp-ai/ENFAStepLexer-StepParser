using Xunit;
using DevelApp.StepLexer;
using System.Collections.Generic;
using System.Linq;

namespace DevelApp.StepLexer.Tests
{
    /// <summary>
    /// Tests for ScopeAwareSymbolTable, SymbolInfo and Reference
    /// </summary>
    public class SymbolTableTests
    {
        private static CodeLocation MakeLocation(string file = "test.cs", int line = 1)
        {
            return new CodeLocation(file, line, 1, line, 10);
        }

        [Fact]
        public void SymbolInfo_Default_HasDefaultValues()
        {
            // Arrange & Act
            var symbol = new SymbolInfo();

            // Assert
            Assert.Equal(string.Empty, symbol.Name);
            Assert.Equal(string.Empty, symbol.Type);
            Assert.Equal(string.Empty, symbol.Scope);
            Assert.NotNull(symbol.Location);
            Assert.False(symbol.CanInline);
            Assert.Null(symbol.Value);
            Assert.Empty(symbol.References);
        }

        [Fact]
        public void Reference_Default_HasDefaultValues()
        {
            // Arrange & Act
            var reference = new Reference();

            // Assert
            Assert.NotNull(reference.Location);
            Assert.Equal(string.Empty, reference.Scope);
            Assert.Equal(string.Empty, reference.Usage);
        }

        [Fact]
        public void Declare_ThenLookupInSameScope_ReturnsSymbol()
        {
            // Arrange
            var table = new ScopeAwareSymbolTable();
            table.Declare("count", "int", "main.method", MakeLocation());

            // Act
            var symbol = table.Lookup("count", "main.method");

            // Assert
            Assert.NotNull(symbol);
            Assert.Equal("count", symbol!.Name);
            Assert.Equal("int", symbol.Type);
            Assert.Equal("main.method", symbol.Scope);
        }

        [Fact]
        public void Lookup_UnknownSymbol_ReturnsNull()
        {
            // Arrange
            var table = new ScopeAwareSymbolTable();
            table.Declare("count", "int", "main", MakeLocation());

            // Act
            var symbol = table.Lookup("missing", "main");

            // Assert
            Assert.Null(symbol);
        }

        [Fact]
        public void Lookup_WalksUpDottedScopeHierarchy()
        {
            // Arrange
            var table = new ScopeAwareSymbolTable();
            table.Declare("count", "int", "main", MakeLocation());

            // Act - lookup from a nested scope should find the parent declaration
            var symbol = table.Lookup("count", "main.method.inner");

            // Assert
            Assert.NotNull(symbol);
            Assert.Equal("main", symbol!.Scope);
        }

        [Fact]
        public void Lookup_UnrelatedScope_ReturnsNull()
        {
            // Arrange
            var table = new ScopeAwareSymbolTable();
            table.Declare("count", "int", "main.method", MakeLocation());

            // Act - "other" does not contain "main" as a prefix
            var symbol = table.Lookup("count", "other.method");

            // Assert
            Assert.Null(symbol);
        }

        [Fact]
        public void Lookup_FallsBackToGlobalScope()
        {
            // Arrange
            var table = new ScopeAwareSymbolTable();
            table.Declare("version", "string", "", MakeLocation());

            // Act
            var symbol = table.Lookup("version", "main.method");

            // Assert
            Assert.NotNull(symbol);
            Assert.Equal("version", symbol!.Name);
        }

        [Fact]
        public void Lookup_ReDeclaration_ReturnsLatestDeclaration()
        {
            // Arrange
            var table = new ScopeAwareSymbolTable();
            table.Declare("count", "int", "main", MakeLocation());
            table.Declare("count", "long", "main", MakeLocation());

            // Act
            var symbol = table.Lookup("count", "main");

            // Assert
            Assert.NotNull(symbol);
            Assert.Equal("long", symbol!.Type);
        }

        [Fact]
        public void GetSymbolsInScope_ReturnsDeclaredSymbols()
        {
            // Arrange
            var table = new ScopeAwareSymbolTable();
            table.Declare("a", "int", "main", MakeLocation());
            table.Declare("b", "int", "main", MakeLocation());
            table.Declare("c", "int", "other", MakeLocation());

            // Act
            var symbols = table.GetSymbolsInScope("main");

            // Assert
            Assert.Equal(2, symbols.Length);
            Assert.Contains(symbols, s => s.Name == "a");
            Assert.Contains(symbols, s => s.Name == "b");
        }

        [Fact]
        public void GetSymbolsInScope_UnknownScope_ReturnsEmpty()
        {
            // Arrange
            var table = new ScopeAwareSymbolTable();

            // Act
            var symbols = table.GetSymbolsInScope("missing");

            // Assert
            Assert.Empty(symbols);
        }

        [Fact]
        public void AddReference_ThenFindAllReferences_ReturnsReference()
        {
            // Arrange
            var table = new ScopeAwareSymbolTable();
            table.Declare("count", "int", "main", MakeLocation());
            var referenceLocation = MakeLocation("test.cs", 5);
            table.AddReference("count", "main", referenceLocation, "read");

            // Act
            var references = table.FindAllReferences("count");

            // Assert
            Assert.Single(references);
            Assert.Equal(referenceLocation, references[0].Location);
            Assert.Equal("main", references[0].Scope);
            Assert.Equal("read", references[0].Usage);
        }

        [Fact]
        public void AddReference_DefaultUsageIsRead()
        {
            // Arrange
            var table = new ScopeAwareSymbolTable();
            table.Declare("count", "int", "main", MakeLocation());

            // Act
            table.AddReference("count", "main", MakeLocation());

            // Assert
            var references = table.FindAllReferences("count");
            Assert.Single(references);
            Assert.Equal("read", references[0].Usage);
        }

        [Fact]
        public void AddReference_UnknownSymbol_IsIgnored()
        {
            // Arrange
            var table = new ScopeAwareSymbolTable();

            // Act
            table.AddReference("missing", "main", MakeLocation());

            // Assert
            Assert.Empty(table.FindAllReferences("missing"));
        }

        [Fact]
        public void FindAllReferences_AggregatesAcrossScopes()
        {
            // Arrange
            var table = new ScopeAwareSymbolTable();
            table.Declare("count", "int", "main.first", MakeLocation());
            table.Declare("count", "int", "main.second", MakeLocation());
            table.AddReference("count", "main.first", MakeLocation("a.cs", 1), "read");
            table.AddReference("count", "main.second", MakeLocation("b.cs", 2), "write");

            // Act
            var references = table.FindAllReferences("count");

            // Assert
            Assert.Equal(2, references.Length);
            Assert.Contains(references, r => r.Scope == "main.first" && r.Usage == "read");
            Assert.Contains(references, r => r.Scope == "main.second" && r.Usage == "write");
        }

        [Fact]
        public void FindAllReferences_NoSymbol_ReturnsEmpty()
        {
            // Arrange
            var table = new ScopeAwareSymbolTable();

            // Act & Assert
            Assert.Empty(table.FindAllReferences("anything"));
        }

        [Fact]
        public void AddReference_ResolvesSymbolThroughScopeHierarchy()
        {
            // Arrange
            var table = new ScopeAwareSymbolTable();
            table.Declare("count", "int", "main", MakeLocation());

            // Act - reference from a nested scope resolves to the parent declaration
            table.AddReference("count", "main.method", MakeLocation("test.cs", 9), "call");

            // Assert
            var references = table.FindAllReferences("count");
            Assert.Single(references);
            Assert.Equal("main.method", references[0].Scope);
            Assert.Equal("call", references[0].Usage);
        }

        [Fact]
        public void ScopeAwareSymbolTable_ImplementsIScopeAwareSymbolTable()
        {
            // Arrange & Act
            IScopeAwareSymbolTable table = new ScopeAwareSymbolTable();
            table.Declare("count", "int", "main", MakeLocation());

            // Assert
            Assert.NotNull(table.Lookup("count", "main"));
        }
    }
}
