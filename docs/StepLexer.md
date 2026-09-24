---
layout: default
title: DevelApp.StepLexer Documentation
---

# DevelApp.StepLexer Documentation

## Overview

DevelApp.StepLexer is a zero-copy, UTF-8 native lexical analyzer designed for high-performance pattern parsing with advanced PCRE2 support. It implements a forward-only parsing architecture with ambiguity resolution capabilities, making it suitable for both regex pattern parsing and source code tokenization.

## Key Features

### 🚀 Zero-Copy Architecture
- **ZeroCopyStringView**: Efficient string slicing without memory allocation
- **UTF-8 Native**: Direct UTF-8 processing without intermediate string conversions
- **Memory Efficient**: Cap'n Proto inspired design patterns for minimal memory footprint

### 🔍 Advanced Pattern Recognition
- **PCRE2 Compatibility**: Comprehensive support for modern regex features
- **Ambiguity Resolution**: Splittable tokens for handling parsing ambiguities
- **Forward-Only Parsing**: No backtracking for predictable performance

### 🏗️ Type-Safe Token System
- **Enumerated Token Types**: Type-safe token classification
- **Position Tracking**: Accurate source location tracking
- **Unicode Support**: Full Unicode code point and property support

### 🌐 Encoding Conversion
- **Library-Based Support**: Uses System.Text.Encoding library for hundreds of encodings
- **No Custom Maintenance**: Delegates encoding logic to .NET's well-maintained encoding infrastructure
- **BOM Detection**: Automatic encoding detection from byte order marks
- **Flexible API**: Support for Encoding objects, encoding names, or code pages
- **Zero-Copy Integration**: Converted data seamlessly integrated into zero-copy processing pipeline

## Core Components

### StepLexer Class

The main lexical analyzer that processes input text and generates tokens using a two-phase approach.

```csharp
public class StepLexer
{
    // Two-phase tokenization methods
    public bool Phase1_LexicalScan(ZeroCopyStringView input);
    public bool Phase2_Disambiguation();
    
    // Configuration
    public void AddRule(TokenRule rule);
  
  public void Initialize(ReadOnlyMemory<byte> input, string fileName = "");
}
```

**Key Methods:**
- `Phase1_LexicalScan()`: Fast lexical analysis with ambiguity detection
- `Phase2_Disambiguation()`: Resolves ambiguities and constructs final token stream
- `AddRule()`: Adds a token rule to the lexer
- `Initialize()`: Initializes the lexer with input data

### SplittableToken Class

Represents tokens that can be split into multiple alternatives during ambiguity resolution.

```csharp
public class SplittableToken
{
    public ZeroCopyStringView Text { get; }
    public TokenType Type { get; set; }
    public int Position { get; }
    public List<SplittableToken>? Alternatives { get; set; }
    
    // Split token into alternatives
    public void Split(params (ZeroCopyStringView text, TokenType type)[] alternatives);
}
```

**Properties:**
- `Text`: Zero-copy view of token content
- `Type`: Enumerated token classification
- `Position`: Character position in source
- `Alternatives`: List of alternative interpretations for ambiguous tokens

### ZeroCopyStringView Struct

Zero-allocation string view for efficient text processing.

```csharp
public readonly struct ZeroCopyStringView : IEquatable<ZeroCopyStringView>
{
    public int Length { get; }
    public bool IsEmpty { get; }
    
    // Efficient slicing without allocation
    public ZeroCopyStringView Slice(int start, int length);
    
    // Direct span access
    public ReadOnlySpan<byte> AsSpan();
    
    // UTF-8 to string conversion when needed
    public override string ToString();
}
```

**Key Features:**
- **Zero Allocation**: Slicing creates views, not copies
- **UTF-8 Native**: Direct byte-level processing
- **Equality Comparison**: Content-based equality checking

### TokenType Enumeration

Token classification system for regex patterns and source code.

```csharp
public enum TokenType
{
    // Regex pattern tokens
    Literal,              // Literal character token in regex patterns
    EscapeSequenc
e,       // Escape sequence token (e.g., \n, \t, \\)
    CharacterClass,       // Character class token (e.g., [a-z], [^0-9])
    GroupStart,           // Group start token (opening parenthesis)
    GroupEnd,             // Group end token (closing parenthesis)
    SpecialGroup,         // Special group token (e.g., (?:), (?=), (?!))
    Quantifier,           // Quantifier token (e.g., *, +, ?, {n,m})
    LazyQuantifier,       // Lazy quantifier token (e.g., *?, +?, ??)
    Alternation,          // Alternation token (pipe symbol |)
    StartAnchor,          // Start anchor token (caret ^)
    EndAnchor,            // End anchor token (dollar sign $)
    AnyChar,              // Any character token (dot .)
    HexEscape,            // Hexadecimal escape token (e.g., \x41)
    UnicodeEscape,        // Unicode escape token (e.g., \u0041, \U00000041)
    UnicodeProperty,      // Unicode property token (e.g., \p{L}, \P{N})
    InlineModifier,       // Inline modifier token (e.g., (?i), (?m), (?s))
    LiteralText,          // Literal text token in \Q...\E construct
    RegexComment,         // Comment token in (?#...) construct
    
    // Source code tokens
    Identifier,           // Identifier token in source code
    Number,               // Numeric literal token
    String,               // String literal token
    Keyword,              // Keyword token (language-specific reserved words)
    Operator,             // Operator token (arithmetic, logical, assignment operators)
    Whitespace,           // Whitespace token (spaces, tabs, line breaks)
    Comment,              // Comment token (single-line and multi-line comments)
    Punctuation           // Punctuation token (semicolons, commas, brackets)
}
```

### PatternParser Class

High-level parser controller for pattern processing with encoding conversion support.

```csharp
public class PatternParser
{
    public PatternParser(ParserType parserType);
    
    // Zero-copy pattern parsing
    public bool ParseP
attern(ReadOnlySpan<byte> utf8Pattern, string terminalName);
    
    // Pattern parsing with encoding conversion
    public bool ParsePattern(ReadOnlySpan<byte> sourceBytes, 
                           Encoding sourceEncoding, 
                           string terminalName);
    
    public bool ParsePattern(ReadOnlySpan<byte> sourceBytes, 
                           string encodingName, 
                           string terminalName);
    
    public bool ParsePattern(ReadOnlySpan<byte> sourceBytes, 
                           int codePage, 
                           string terminalName);
    
    // Stream-based parsing with encoding
    public bool ParsePatternFromStream(Stream stream, 
                                      Encoding sourceEncoding, 
                                      string terminalName);
    
    public bool ParsePatternFromStreamWithAutoDetect(Stream stream, 
                                                     string terminalName);
    
    // Access parsed tokens
    public List<SplittableToken> GetTokens();
}
```

**Parser Types:**
- `ParserType.Regex`: Regular expression pattern parsing
- `ParserType.Grammar`: Grammar-based pattern parsing

### EncodingConverter Class

Library-based converter using System.Text.Encoding for hundreds of character encodings.

```csharp
public static class EncodingConverter
{
    // Convert from any encoding to UTF-8 using Encoding object
    public static byte[] ConvertToUTF8(ReadOnlySpan<byte> sourceBytes, 
                                       Encoding sourceEncoding);
    
    // Convert using encoding name (e.g., "shift_jis", "GB2312", "ISO-8859-1")
    public static byte[] ConvertToUTF8(ReadOnlySpan<byte> sourceBytes, 
                                       string encodingName);
    
    // Convert using code page number (e.g., 932 for shift_jis, 1252 for Windows-1252)
    public static byte[] ConvertToUTF8(ReadOnlySpan<byte> sourceBytes, 
                                       int codePage);
    

    // Auto-detect encoding from BOM and convert
    public static byte[] ConvertToUTF8WithAutoDetect(ReadOnlySpan<byte> sourceBytes);
    
    // Detect encoding from BOM
    public static (Encoding encoding, int bomLength) DetectEncodingFromBOM(ReadOnlySpan<byte> bytes);
    
    // Utility methods
    public static EncodingInfo[] GetAvailableEncodings();
    public static bool IsEncodingAvailable(string encodingName);
    public static bool IsEncodingAvailable(int codePage);
}
```

**Key Features:**
- **Library-Based**: Uses System.Text.Encoding with CodePages provider for 100+ encodings
- **No Maintenance Burden**: Delegates encoding logic to .NET's well-maintained infrastructure
- **BOM Detection**: Automatically identifies UTF-8, UTF-16 LE/BE, UTF-32 LE/BE from byte order marks
- **Flexible API**: Accept Encoding objects, names ("shift_jis", "GB2312"), or code pages (932, 936)
- **Comprehensive Support**: Supports all encodings available in .NET including:
  - Unicode family: UTF-8, UTF-16, UTF-32
  - Western: ISO-8859-1 through 15, Windows-1252
  - Eastern: Shift-JIS, EUC-JP, GB2312, Big5, EUC-KR
  - Cyrillic: Windows-1251, KOI8-R, ISO-8859-5
  - Arabic, Hebrew, Thai, Vietnamese, and many more

**Example Encodings:**
```csharp
// By Encoding object
var bytes = EncodingConverter.ConvertToUTF8(sourceBytes, Encoding.UTF8);

// By name (hundreds supported!)
var shiftJIS = EncodingConverter.ConvertToUTF8(sourceBytes, "shift_jis");
var gb2312 = EncodingConverter.ConvertToUTF8(sourceBytes, "GB2312");
var latin1 = EncodingConverter.ConvertToUTF8(sourceBytes, "ISO-8859-1");

// By code page
var windows1252 = EncodingConverter.ConvertToUTF8(sourceBytes, 1252);
var shiftJIS932 = EncodingConverter.ConvertToUTF8(sourceBytes, 932);

// Auto-detect from BOM
var autoDetected = EncodingConverter.ConvertToUTF8WithAutoDetect(sourceBytes);
```

## Advanced Features

### Ambiguity Resolution

The StepLexer handles parsing ambiguities through token splitting:

```csharp
// Exampl
e: Ambiguous quantifier interpretation
var token = new SplittableToken(view, TokenType.Literal, 0);

// Split into alternatives when ambiguity detected
token.Split(
    (view.Slice(0, 1), TokenType.Literal),
    (view.Slice(0, 2), TokenType.RangeCount)
);
```

### Unicode Support

Comprehensive Unicode handling with zero-copy efficiency:

```csharp
// Unicode code points: \x{1F600}
// Unicode properties: \p{L}, \P{N}
// Unicode newlines: \R (any Unicode newline sequence)
```

### Performance Optimization

- **Forward-Only Parsing**: No backtracking for predictable performance
- **Zero-Copy Operations**: Minimal memory allocations
- **UTF-8 Native**: Direct byte processing without encoding conversions
- **Lazy Evaluation**: Tokens processed on-demand

## Usage Examples

### Basic Tokenization

```csharp
using DevelApp.StepLexer;
using System.Text;

// Create pattern parser
var parser = new PatternParser(ParserType.Regex);

// Prepare UTF-8 input
var pattern = @"\d{2,4}-\w+@[a-z]+\.com";
var utf8Data = Encoding.UTF8.GetBytes(pattern);

// Parse the pattern
bool success = parser.ParsePattern(utf8Data.AsSpan(), "email_pattern");

if (success)
{
    var results = parser.GetResults();
    Console.WriteLine($"Phase 1 tokens: {results.Phase1TokenCount}");
    Console.WriteLine($"Ambiguous tokens: {results.AmbiguousTokenCount}");
    Console.WriteLine($"Parsing: {(success ? "SUCCESS" : "FAILED")}");
}
```

### Pattern Parser Usage

```csharp
using DevelApp.StepLexer;

// Create pattern parser for regex
var parser = new PatternParser(ParserType.Regex);

// Parse pattern with zero-copy
var pattern = @"[a-zA-Z][a-zA-Z0-9]*";
var utf8Pattern = Encoding.UTF8.GetBytes(pattern);

bool success = parser.ParsePattern(utf8Pattern, "identifier");

if (success)
{
    var results = parser.GetResults();
    Console.WriteLine($"Pattern parsed successfully!");
    Console.WriteLine($"Phase 1 tokens: {results.Phase1TokenCount}");
}
```

### Unicode Pattern Processing

```csharp
// Unicode-awa
re pattern
var unicodePattern = @"\p{L}+\x{20}\p{N}+";
var utf8Data = Encoding.UTF8.GetBytes(unicodePattern);

var parser = new PatternParser(ParserType.Regex);
bool success = parser.ParsePattern(utf8Data.AsSpan(), "unicode_pattern");

if (success)
{
    Console.WriteLine("Unicode pattern parsed successfully!");
}
```

### Encoding Conversion

#### Converting from Various Encodings to UTF-8

```csharp
using DevelApp.StepLexer;
using System.Text;

// Pattern in UTF-16 format
var pattern = @"\d{2,4}-\w+";
var utf16Bytes = Encoding.Unicode.GetBytes(pattern);

// Convert to UTF-8 and parse using Encoding object
var parser = new PatternParser(ParserType.Regex);
bool success = parser.ParsePattern(utf16Bytes, Encoding.Unicode, "pattern");

// Or use encoding by name
var shiftJISBytes = Encoding.GetEncoding("shift_jis").GetBytes(pattern);
bool sjisSuccess = parser.ParsePattern(shiftJISBytes, "shift_jis", "pattern");

// Or use code page number
var windows1252Bytes = Encoding.GetEncoding(1252).GetBytes(pattern);
bool cpSuccess = parser.ParsePattern(windows1252Bytes, 1252, "pattern");

if (success)
{
    var tokens = parser.GetTokens();
    // Process tokens...
}
```

#### Auto-Detecting Encoding from Stream

```csharp
using DevelApp.StepLexer;
using System.IO;

// Read pattern from file with BOM for auto-detection
var parser = new PatternParser(ParserType.Regex);
using var stream = File.OpenRead("pattern.txt");

// Auto-detect encoding from BOM and parse
bool success = parser.ParsePatternFromStreamWithAutoDetect(stream, "pattern");

if (success)
{
    Console.WriteLine("Pattern parsed successfully!");
}
```

#### Manual Encoding Detection and Conversion

```csharp
using DevelApp.StepLexer;

// Read bytes from any source
byte[] sourceBytes = File.ReadAllBytes("pattern.dat");

// Detect encoding from BOM
var (encoding, bomLength) = EncodingConverter.DetectEncodingFromBOM(sourceBytes);
Console.WriteLine($"Detected encoding: {encoding.EncodingName}, BOM: {bomLength} bytes");

//
 Convert to UTF-8
byte[] utf8Bytes = EncodingConverter.ConvertToUTF8(sourceBytes, encoding);

// Parse as UTF-8
var parser = new PatternParser(ParserType.Regex);
parser.ParsePattern(utf8Bytes, "pattern");
```

#### Working with Any Encoding by Name

```csharp
using DevelApp.StepLexer;
using System.IO;
using System.Text;

// Check if an encoding is available
if (EncodingConverter.IsEncodingAvailable("GB2312"))
{
    // Pattern file encoded in GB2312 (Simplified Chinese)
    byte[] gb2312Bytes = File.ReadAllBytes("chinese_pattern.txt");
    var parser = new PatternParser(ParserType.Regex);
    
    // Convert and parse using encoding name
    bool success = parser.ParsePattern(gb2312Bytes, "GB2312", "chinese_pattern");
    
    if (success)
    {
        Console.WriteLine("GB2312 pattern processed successfully!");
    }
}
```

#### Discovering Available Encodings

```csharp
using DevelApp.StepLexer;

// Get all available encodings
var encodings = EncodingConverter.GetAvailableEncodings();

Console.WriteLine($"Total encodings available: {encodings.Length}");
Console.WriteLine("\nSample encodings:");

foreach (var encoding in encodings.Take(10))
{
    Console.WriteLine($"  {encoding.Name} (Code Page: {encoding.CodePage}) - {encoding.DisplayName}");
}

// Output examples:
//   utf-8 (Code Page: 65001) - Unicode (UTF-8)
//   shift_jis (Code Page: 932) - Japanese (Shift-JIS)
//   GB2312 (Code Page: 936) - Chinese Simplified (GB2312)
//   ISO-8859-1 (Code Page: 28591) - Western European (ISO)
//   windows-1252 (Code Page: 1252) - Western European (Windows)
```

#### Batch Conversion of Multiple Encodings

```csharp
using DevelApp.StepLexer;
using System.Text;

// Process patterns from different sources
var patterns = new[]
{
    (File.ReadAllBytes("pattern_utf8.txt"), Encoding.UTF8),
    (File.ReadAllBytes("pattern_utf16.txt"), Encoding.Unicode),
    (File.ReadAllBytes("pattern_sjis.txt"), Encoding.GetEncoding("shift_jis")),
    (File.ReadAllBytes("pattern_gb2312.txt"), Enc
oding.GetEncoding("GB2312"))
};

var parser = new PatternParser(ParserType.Regex);

foreach (var (bytes, encoding) in patterns)
{
    if (parser.ParsePattern(bytes, encoding, "pattern"))
    {
        Console.WriteLine($"Successfully parsed {encoding.EncodingName} pattern");
    }
}
```

## Error Handling

The StepLexer provides comprehensive error handling:

```csharp
try
{
    var tokens = lexer.TokenizeRegexPattern(input);
}
catch (ENFA_RegexBuild_Exception ex)
{
    Console.WriteLine($"Regex parsing error: {ex.Message}");
    Console.WriteLine($"Position: {ex.Location}");
}
catch (ENFA_Exception ex)
{
    Console.WriteLine($"General lexer error: {ex.Message}");
}
```

## Design Principles

### 1. Zero-Copy Performance
- All string operations use `ZeroCopyStringView`
- Minimal memory allocations during tokenization
- Direct UTF-8 processing without intermediate conversions

### 2. Forward-Only Architecture
- No backtracking to ensure predictable performance
- Conflicts with complex regex features (atomic grouping, recursion)
- Alternative approaches for complex constructs

### 3. Type Safety
- Enumerated token types prevent classification errors
- Compile-time verification of token handling
- Clear separation between different token categories

### 4. Unicode First
- Native UTF-8 processing throughout
- Full Unicode code point support
- Advanced Unicode property classes

## Limitations

### By Design Exclusions

The StepLexer intentionally excludes certain PCRE2 features that conflict with its forward-only architecture:

1. **Atomic Grouping** (`(?>...)`)
   - Requires backtracking prevention mechanisms
   - Conflicts with forward-only parsing paradigm

2. **Possessive Quantifiers** (`*+`, `++`, `?+`)
   - Similar to atomic grouping requirements
   - Would compromise zero-copy performance

3. **Recursive Patterns** (`(?R)`, `(?&name)`)
   - Adds complexity to lexer architecture
   - Better handled by grammar-based StepParser

These limitations are architectural d
ecisions that maintain the lexer's performance and simplicity advantages.

## Integration with StepParser

The StepLexer integrates seamlessly with DevelApp.StepParser for complete parsing solutions:

```csharp
// Lexer tokenizes input
var tokens = stepLexer.TokenizeRegexPattern(pattern);

// Parser builds parse trees and semantic graphs
var parseResult = stepParser.Parse(tokens);
var cognitiveGraph = parseResult.CognitiveGraph;
```

## Testing

Comprehensive test coverage includes:

- **Zero-copy operations**: Memory allocation verification
- **Unicode handling**: Multi-byte character processing
- **Error conditions**: Invalid pattern handling
- **Performance benchmarks**: Throughput and memory usage
- **PCRE2 compliance**: Feature compatibility testing

## Best Practices

1. **Use UTF-8 Input**: Avoid string-to-byte conversions when possible
2. **Reuse Lexer Instances**: Initialize once, tokenize multiple patterns
3. **Handle Alternatives**: Check for `SplittableToken.Alternatives` in ambiguous cases
4. **Error Recovery**: Implement robust error handling for invalid patterns
5. **Performance Monitoring**: Profile memory usage for large-scale processing

## Learned Token-Rule Prioritization (issue #74)

The lexer supports an optional ML-assisted **evaluation ordering** for token
rules (`ILearnedRulePrioritizer`). When a prioritizer is installed, rules are
evaluated in descending predicted match probability instead of declaration
order. This is an ordering-only optimization:

- every applicable rule is still attempted — the prioritizer never decides
  match/no-match;
- the collected matches are processed in declaration order, so tokens, paths
  and diagnostics stay bit-identical to the default behavior;
- the feature is OFF by default; the StepParser only installs the prioritizer
  while `MlAssistFeature.LearnedRulePrioritization` is enabled (see
  `MlAssistOptions`, and the issue #58 ML plan).

The prototype ships a managed linear model (`LearnedRulePrioritizer.Default`,
model version `0.1.0`) over cheap rule/byte features. It is intended to be
replaced by weights trained offline on `ml-trace/1` corpus traces (PR #64
harness). Always-on counters (`RuleMatchDiagnostics.TotalMatchAttempts`,
`WastedMatchAttempts`, `WastedAttemptRatio`) make the effect measurable:
ordering shrinks the attempts wasted before the first successful match.

## See Also

- [DevelApp.StepParser Documentation](StepParser.md)
- [PCRE2 Support Matrix](PCRE2-Support.md)
- [Grammar File Creation Guide](Grammar_File_Creation_Guide.md)