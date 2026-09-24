---
layout: default
title: DevelApp.StepParser Documentation
---

# DevelApp.StepParser Documentation

## Overview

DevelApp.StepParser is a modern, CognitiveGraph-integrated parser engine that processes tokens from DevelApp.StepLexer to build semantic representations of source code and patterns. It implements GLR-style parsing with context-sensitive grammar support, making it ideal for Domain-Specific Language (DSL) development and advanced code analysis.

## Key Features

### 🧠 CognitiveGraph Integration
- **Semantic Analysis**: Automatic semantic graph construction during parsing
- **Symbol Table Management**: Scope-aware symbol tracking and resolution
- **Contextual Parsing**: Hierarchical context stack for complex language constructs
- **Refactoring Support**: Built-in code transformation capabilities

### 🔧 Advanced Grammar Support
- **GLR-Style Parsing**: Handles ambiguous grammars efficiently
- **Context-Sensitive Rules**: Different parsing behavior based on context
- **Precedence & Associativity**: Operator precedence resolution
- **Grammar Inheritance**: Reusable grammar components

### 🚀 High Performance
- **Step-by-Step Processing**: Incremental parsing for real-time feedback
- **Memory Efficient**: Optimized data structures for large codebases
- **Error Recovery**: Robust error handling with detailed diagnostics
- **Caching**: Parse result caching for improved performance

## Core Components

### StepParserEngine Class

The main parser engine that coordinates all parsing operations.

```csharp
public class StepParserEngine
{
    // Grammar management
    public void LoadGrammar(string grammarFile);
    public void LoadGrammarFromContent(string grammarContent, string fileName = "inline");
    public GrammarDefinition? CurrentGrammar { get; }
    
    // Parsing operations
    public StepParsingResult Parse(string input, string fileName = "");
    public CognitiveGraph.CognitiveGraph ParseAndMerge(
        CognitiveGraph.CognitiveGraph existingGraph, 
        string input, 
        string fileName = "");
    public Step
ParsingResult ParseMultipleFiles(Dictionary<string, string> files);
    
    // Context management
    public ParseContext Context { get; }
}
```

**Key Methods:**
- `LoadGrammarFromFile()`: Load grammar definition from file
- `LoadGrammarFromContent()`: Load grammar from string content
- `Parse()`: Parse source code or token stream
- Context management methods for scope-aware parsing

### GrammarDefinition Class

Represents a complete grammar definition loaded from grammar files.

```csharp
public class GrammarDefinition
{
    public string Name { get; set; }
    public string TokenSplitter { get; set; }
    public List<TokenRule> TokenRules { get; set; }
    public List<ProductionRule> ProductionRules { get; set; }
    public Dictionary<string, int> Precedence { get; set; }
    public Dictionary<string, string> Associativity { get; set; }
    public List<string> Contexts { get; set; }
    public bool IsInheritable { get; set; }
    public string FormatType { get; set; }
}
```

**Properties:**
- `TokenRules`: Lexical analysis rules (regex patterns, literals)
- `ProductionRules`: Syntax analysis rules (grammar productions)
- `Precedence`: Operator precedence values
- `Associativity`: Left/right associativity rules
- `Contexts`: Available parsing contexts

### TokenRule Class

Defines lexical analysis rules for token recognition.

```csharp
public class TokenRule
{
    public string Name { get; set; }
    public string Pattern { get; set; }
    public TokenRuleType Type { get; set; }
    public string? Context { get; set; }
    public Dictionary<string, object> Actions { get; set; }
    public int Priority { get; set; }
}
```

**Token Rule Types:**
- `RegexPattern`: Regular expression patterns (`/[0-9]+/`)
- `LiteralString`: Exact string matches (`'+'`, `"class"`)
- `ContextSensitive`: Rules that apply only in specific contexts

### ProductionRule Class

Defines syntax analysis rules for parsing.

```csharp
public class ProductionRule
{
    public string LeftHandSide
 { get; set; }
    public List<List<string>> RightHandSides { get; set; }
    public string? Context { get; set; }
    public Dictionary<string, object> SemanticActions { get; set; }
    public int Precedence { get; set; }
}
```

**Features:**
- Multiple right-hand sides for alternatives (`|` in grammar notation)
- Context-sensitive rules for different parsing contexts
- Semantic actions for CognitiveGraph integration
- Precedence values for conflict resolution

### StepParsingResult Class

Contains the complete result of a parsing operation.

```csharp
public class StepParsingResult
{
    public bool Success { get; set; }
    public List<StepToken> Tokens { get; set; }
    public ParseTree? ParseTree { get; set; }
    public CognitiveGraph.CognitiveGraph? CognitiveGraph { get; set; }
    public List<string> Errors { get; set; }
    public List<string> Warnings { get; set; }
    public Dictionary<string, object> Metadata { get; set; }
}
```

**Result Components:**
- `Tokens`: Generated token stream from lexical analysis
- `ParseTree`: Hierarchical parse tree structure
- `CognitiveGraph`: Semantic representation for analysis
- `Errors/Warnings`: Diagnostic information

### IContextStack Interface

Manages hierarchical parsing contexts for complex language features.

```csharp
public interface IContextStack
{
    void Push(string context, string? name = null);
    void Pop();
    string Current();
    bool InScope(string context);
    int Depth();
    string[] GetPath();
    bool Contains(string context);
}
```

**Context Management:**
- **Hierarchical Contexts**: Nested scope management
- **Named Contexts**: Optional context naming for debugging
- **Scope Queries**: Check if contexts are in scope
- **Path Tracking**: Full context path from root

### IScopeAwareSymbolTable Interface

Provides scope-aware symbol table management.

```csharp
public interface IScopeAwareSymbolTable
{
    void Declare(string name, string type, string scope, ICodeLocation location);
    Sy
mbolEntry? Lookup(string name, string scope);
    bool Exists(string name, string scope);
    List<SymbolEntry> GetSymbolsInScope(string scope);
    void EnterScope(string scopeName);
    void ExitScope();
}
```

**Symbol Management:**
- **Scope-Aware Declarations**: Symbols tracked by scope
- **Hierarchical Lookup**: Symbol resolution through scope chain
- **Location Tracking**: Source location for all symbols
- **Scope Navigation**: Enter/exit scope operations

## Grammar File Format

The StepParser uses a declarative grammar format for defining DSLs:

```
Grammar: MyLanguage
TokenSplitter: Space
FormatType: EBNF

# Token Rules (Lexical Analysis)
<NUMBER> ::= /[0-9]+(\.[0-9]+)?/
<IDENTIFIER> ::= /[a-zA-Z][a-zA-Z0-9]*/
<STRING> ::= /"([^"\\]|\\.)*"/
<PLUS> ::= '+'
<MINUS> ::= '-'
<TIMES> ::= '*'
<DIVIDE> ::= '/'
<ASSIGN> ::= '='
<LPAREN> ::= '('
<RPAREN> ::= ')'
<WS> ::= /[ \t\r\n]+/ => { skip }

# Production Rules (Syntax Analysis)
<program> ::= <statement_list>

<statement_list> ::= <statement>
                  | <statement_list> <statement>

<statement> ::= <assignment>
             | <expression>

<assignment> ::= <IDENTIFIER> <ASSIGN> <expression>

<expression> ::= <term>
              | <expression> <PLUS> <term>
              | <expression> <MINUS> <term>

<term> ::= <factor>
        | <term> <TIMES> <factor>
        | <term> <DIVIDE> <factor>

<factor> ::= <NUMBER>
          | <IDENTIFIER>
          | <LPAREN> <expression> <RPAREN>

# Precedence and Associativity
%precedence <TIMES> <DIVIDE> 10
%precedence <PLUS> <MINUS> 5
%left <PLUS> <MINUS> <TIMES> <DIVIDE>
%right <ASSIGN>
```

### Grammar Header

```
Grammar: MyLanguage          # Grammar name (required)
TokenSplitter: Space         # Token splitting strategy
FormatType: EBNF            # Grammar format type
Inheritable: true           # Allow inheritance
```

### Token Rules

```
# Regular expression patterns
<NUMBER> ::= /[0-9]+/

# Literal strings
<PLUS> ::= '+'
<CLASS> ::= "class"

# Context-sensit
ive rules
<STRING_CONTENT[string]> ::= /[^"]*/

# Actions
<WS> ::= /[ \t\r\n]+/ => { skip }
```

### Production Rules

```
# Basic productions
<expr> ::= <term>

# Alternatives with |
<statement> ::= <assignment>
             | <expression>
             | <block>

# Semantic actions
<assignment> ::= <IDENTIFIER> <ASSIGN> <expression> => {
    symbol_table.declare($1.value, $3.type);
    cognitive_graph.add_assignment_node($1, $3);
}
```

## Advanced Features

### Context-Sensitive Parsing

The StepParser supports different parsing rules based on context:

```
# Different rules in different contexts
<expression> ::= <term>
<expression[function]> ::= <call> | <term>
<expression[class]> ::= <member_access> | <term>

# Context transitions
<string_start> ::= '"' => { enter_context: string }
<string_end[string]> ::= '"' => { exit_context }
```

### Ambiguity Resolution

GLR-style parsing handles ambiguous grammars:

```csharp
// Grammar with ambiguous expressions
// 1 + 2 * 3 can be parsed as:
// - (1 + 2) * 3 (if + has higher precedence)
// - 1 + (2 * 3) (if * has higher precedence)

// Precedence resolves ambiguity
%precedence <TIMES> 10
%precedence <PLUS> 5
```

### CognitiveGraph Integration

Automatic semantic analysis during parsing:

```csharp
// Parse source code
var result = stepParser.Parse(sourceCode);
var cognitiveGraph = result.CognitiveGraph;

// Query semantic information
var variables = cognitiveGraph.Query("variable_declaration");
var functions = cognitiveGraph.Query("function_definition");
var dependencies = cognitiveGraph.Query("dependency_relationship");
```

### Refactoring Operations

Built-in support for code transformations:

```csharp
public class RefactoringOperation
{
    public string Name { get; set; }
    public string[] ApplicableContexts { get; set; }
    public Func<ParseContext, bool>? Preconditions { get; set; }
    public Func<ICodeLocation, ParseContext, RefactoringResult>? Execute { get; set; }
}

// Example: Rename variable refactori
ng
var renameOp = new RefactoringOperation
{
    Name = "Rename Variable",
    ApplicableContexts = new[] { "variable_declaration", "variable_reference" },
    Preconditions = context => context.SymbolTable.Exists(context.SelectedSymbol),
    Execute = (location, context) => RenameVariable(location, context)
};
```

## Usage Examples

### Basic Grammar Loading and Parsing

```csharp
using DevelApp.StepParser;
using DevelApp.StepLexer;

// Create parser engine
var engine = new StepParserEngine();

// Load grammar from file
engine.LoadGrammar("my_language.grammar");

// Parse source code
var sourceCode = @"
x = 10 + 20;
y = x * 2;
";

var result = engine.Parse(sourceCode);

if (result.Success)
{
    Console.WriteLine("Parse successful!");
    Console.WriteLine($"Tokens: {result.Tokens.Count}");
    
    // Access semantic information
    var cognitiveGraph = result.CognitiveGraph;
    if (cognitiveGraph != null)
    {
        Console.WriteLine("CognitiveGraph constructed successfully!");
    }
}
else
{
    Console.WriteLine("Parse failed:");
    foreach (var error in result.Errors)
    {
        Console.WriteLine($"  {error}");
    }
}
```

### Context-Sensitive Parsing

```csharp
// Grammar with context-sensitive rules
var grammar = @"
Grammar: ContextExample
TokenSplitter: Space

<IDENTIFIER> ::= /[a-zA-Z][a-zA-Z0-9]*/
<DOT> ::= '.'
<LPAREN> ::= '('
<RPAREN> ::= ')'

# Different behavior in different contexts
<expression> ::= <simple_expr>
<expression[method_call]> ::= <IDENTIFIER> <LPAREN> <args> <RPAREN>
<expression[member_access]> ::= <IDENTIFIER> <DOT> <IDENTIFIER>

<simple_expr> ::= <IDENTIFIER>
";

engine.LoadGrammarFromContent(grammar);

// Parse with context awareness
var result = engine.Parse("myMethod(arg1, arg2)");

// Access the parse context
var context = engine.Context;
Console.WriteLine($"Current context: {context.ContextStack.Current()}");
```
```

### Symbol Table Integration

```csharp
// Access symbol table from parse result
var symbolTable = resu
lt.CognitiveGraph?.SymbolTable;

// Declare symbols during parsing
symbolTable?.Declare("myVariable", "int", "global", codeLocation);

// Look up symbols
var symbol = symbolTable?.Lookup("myVariable", "global");
if (symbol != null)
{
    Console.WriteLine($"Symbol: {symbol.Name}, Type: {symbol.Type}");
}
```

### Error Recovery

```csharp
// Grammar with error recovery
var grammar = @"
<statement> ::= <assignment>
             | <expression>
             | error ';' => { report_error(""Invalid statement""); }
";

// Parse code with syntax errors
var result = engine.Parse("x = ; y = 10;"); // Invalid assignment

// Check for errors and warnings
if (!result.Success)
{
    foreach (var error in result.Errors)
    {
        Console.WriteLine($"Error: {error}");
    }
    
    foreach (var warning in result.Warnings)
    {
        Console.WriteLine($"Warning: {warning}");
    }
}
```

## Error Handling

The StepParser provides comprehensive error handling:

```csharp
try
{
    var result = engine.Parse(sourceCode);
}
catch (ENFA_GrammarBuild_Exception ex)
{
    Console.WriteLine($"Grammar build error: {ex.Message}");
    Console.WriteLine($"Location: {ex.Location}");
}
catch (ENFA_Exception ex)
{
    Console.WriteLine($"General parser error: {ex.Message}");
}
```

### Error Types

- **Grammar Build Errors**: Invalid grammar definitions
- **Parse Errors**: Syntax errors in input code
- **Semantic Errors**: Type checking and symbol resolution errors
- **Context Errors**: Invalid context transitions

## Design Principles

### 1. Semantic-First Approach
- Automatic CognitiveGraph construction
- Symbol table integration
- Semantic action support

### 2. Context Awareness
- Hierarchical context management
- Context-sensitive parsing rules
- Scope-aware symbol resolution

### 3. Extensibility
- Grammar inheritance
- Pluggable semantic actions
- Custom refactoring operations

### 4. Performance
- Incremental parsing
- Result caching
- Memory-efficient data structures

## Integra
tion with StepLexer

The StepParser works seamlessly with DevelApp.StepLexer:

```csharp
// StepLexer tokenizes input
var lexer = new StepLexer();
var tokens = lexer.TokenizeSource(sourceCode);

// StepParser builds semantic representation
var parseResult = stepParser.Parse(tokens);

// Access both lexical and semantic information
var tokenStream = parseResult.Tokens;
var semanticGraph = parseResult.CognitiveGraph;
```

## Testing

Comprehensive test coverage includes:

- **Grammar Loading**: Valid and invalid grammar files
- **Parse Tree Construction**: Correct tree structure verification
- **CognitiveGraph Integration**: Semantic analysis accuracy
- **Error Recovery**: Robust error handling
- **Context Management**: Scope and context correctness
- **Performance**: Large codebase handling

## Best Practices

1. **Grammar Design**: Use clear, unambiguous production rules
2. **Context Management**: Minimize context switching overhead
3. **Error Recovery**: Implement robust error recovery rules
4. **Symbol Tables**: Use appropriate scoping strategies
5. **Performance**: Profile parsing for large inputs
6. **Testing**: Validate grammars with comprehensive test suites

## Limitations

### Excluded Features

For consistency with StepLexer's forward-only architecture:

1. **Complex Backtracking**: Limited backtracking support
2. **Highly Ambiguous Grammars**: Some ambiguities may not resolve efficiently
3. **Dynamic Grammar Modification**: Grammar structure is fixed after loading

These limitations maintain parsing performance and predictability.

## See Also

- [DevelApp.StepLexer Documentation](StepLexer.md)
- [Grammar File Creation Guide](Grammar_File_Creation_Guide.md)
- [PCRE2 Support Matrix](PCRE2-Support.md)