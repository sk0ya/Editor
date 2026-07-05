namespace Editor.Core.Matchit;

/// <summary>
/// Describes matchit-style keyword-chain matching for a language: `%` on a keyword
/// belonging to one of these chains jumps to the sibling keyword occupying the
/// matching position in the same construct (e.g. `if`/`elseif`/`else`/`end`).
/// </summary>
public interface IMatchitLanguage
{
    string[] Extensions { get; }

    /// <summary>
    /// Ordered chains of keyword "slots". Each slot lists the acceptable spellings for
    /// that position in the chain (e.g. <c>[ ["if"], ["elseif"], ["else"], ["end"] ]</c>,
    /// or <c>[ ["#if","#ifdef","#ifndef"], ["#elif"], ["#else"], ["#endif"] ]</c> when a
    /// position accepts several spellings). Only the first slot (opener) and the last
    /// slot (closer) are structurally significant; everything in between is a "middle".
    /// </summary>
    IReadOnlyList<string[][]> KeywordChains { get; }

    /// <summary>Extra characters (beyond letters/digits/underscore) that are part of this
    /// language's keyword tokens, e.g. '#' for C preprocessor directives.</summary>
    char[] ExtraWordChars => [];
}
