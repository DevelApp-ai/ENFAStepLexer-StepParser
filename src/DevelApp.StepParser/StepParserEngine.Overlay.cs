using System;

namespace DevelApp.StepParser
{
    /// <summary>
    /// Grammar overlay/composition support on the engine
    /// (ENFAStepLexer-StepParser issue #66): load a base grammar together
    /// with an overlay grammar, or apply an overlay to an already-loaded
    /// grammar, producing a composite grammar without authoring a derived
    /// grammar file.
    /// </summary>
    public partial class StepParserEngine
    {
        /// <summary>
        /// Gets the result of the most recent grammar merge performed by
        /// <see cref="LoadGrammarWithOverlay"/> or <see cref="ApplyOverlay"/>,
        /// or null when the current grammar was loaded without an overlay.
        /// </summary>
        public GrammarMergeResult? LastMergeResult { get; private set; }

        /// <summary>
        /// Load a grammar composed from a base grammar and an overlay grammar.
        /// The composite grammar is produced by
        /// <see cref="GrammarLoader.ComposeWithOverlayContent"/> with the
        /// given conflict resolution strategy.
        /// </summary>
        /// <param name="baseGrammarContent">The grammar file content of the base grammar (e.g. a target language grammar).</param>
        /// <param name="overlayGrammarContent">The grammar file content of the overlay grammar (e.g. Labyrinth pattern tokens).</param>
        /// <param name="conflictResolution">How name collisions between base and overlay are resolved (default: overlay wins).</param>
        /// <param name="baseFileName">File name used for error reporting of the base grammar.</param>
        /// <param name="overlayFileName">File name used for error reporting of the overlay grammar.</param>
        public void LoadGrammarWithOverlay(
            string baseGrammarContent,
            string overlayGrammarContent,
            OverlayConflictResolution conflictResolution = OverlayConflictResolution.OverlayWins,
            string baseFileName = "base.grammar",
            string overlayFileName = "overlay.grammar")
        {
            var result = _grammarLoader.ComposeWithOverlayContent(baseGrammarContent, overlayGrammarContent, conflictResolution, baseFileName, overlayFileName);
            _currentGrammar = result.Grammar;
            LastMergeResult = result;
            ConfigureLexerAndParser();
            RegisterDefaultRefactoringOperations();
        }

        /// <summary>
        /// Apply an overlay grammar to the currently loaded grammar and
        /// reconfigure the engine with the composite grammar. The previously
        /// loaded grammar is not mutated.
        /// </summary>
        /// <param name="overlayGrammarContent">The grammar file content of the overlay grammar.</param>
        /// <param name="conflictResolution">How name collisions between the loaded grammar and the overlay are resolved (default: overlay wins).</param>
        /// <param name="overlayFileName">File name used for error reporting of the overlay grammar.</param>
        /// <returns>The merge result containing the composite grammar and the resolved conflicts.</returns>
        /// <exception cref="InvalidOperationException">Thrown when no grammar has been loaded yet.</exception>
        public GrammarMergeResult ApplyOverlay(
            string overlayGrammarContent,
            OverlayConflictResolution conflictResolution = OverlayConflictResolution.OverlayWins,
            string overlayFileName = "overlay.grammar")
        {
            if (_currentGrammar is null)
            {
                throw new InvalidOperationException("Cannot apply an overlay before a grammar has been loaded. Call LoadGrammar or LoadGrammarFromContent first.");
            }

            var overlay = _grammarLoader.ParseGrammarContent(overlayGrammarContent, overlayFileName);
            var result = _grammarLoader.ComposeGrammars(_currentGrammar, overlay, conflictResolution);
            _currentGrammar = result.Grammar;
            LastMergeResult = result;
            ConfigureLexerAndParser();
            return result;
        }

        /// <summary>
        /// Load an already-built <see cref="GrammarDefinition"/> (e.g. the
        /// merged grammar from <see cref="GrammarLoader.ComposeGrammars"/>)
        /// into the engine.
        /// </summary>
        /// <param name="grammar">The grammar definition to load.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="grammar"/> is null.</exception>
        public void LoadGrammarDefinition(GrammarDefinition grammar)
        {
            _currentGrammar = grammar ?? throw new ArgumentNullException(nameof(grammar));
            LastMergeResult = null;
            ConfigureLexerAndParser();
            RegisterDefaultRefactoringOperations();
        }
    }
}
