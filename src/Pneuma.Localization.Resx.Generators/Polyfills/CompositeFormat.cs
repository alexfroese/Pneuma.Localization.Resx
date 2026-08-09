namespace Pneuma.Localization.Resx.Generators.Polyfills;

// copied from https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Text/CompositeFormat.cs
//
// modified for my purposes, and to be compatible with netstandard2.0

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

/// <summary>Represents a parsed composite format string.</summary>
[DebuggerDisplay("{Format}")]
internal sealed class CompositeFormat
{
    /// <summary>The number of args required to satisfy the format holes.</summary>
    /// <remarks>This is equal to one more than the largest index required by any format hole.</remarks>
    private readonly int _argsRequired;

    /// <summary>Initializes the instance.</summary>
    /// <param name="format">The composite format string that was parsed.</param>
    /// <param name="segments">The parsed segments.</param>
    private CompositeFormat(
        string format,
        (string? Literal, int ArgIndex, int Alignment, string? Format)[] segments
    )
    {
        // Store the format.
        Format = format;

        // Compute derivative information from the segments.
        int literalLength = 0,
            formattedCount = 0,
            argsRequired = 0;
        foreach (var segment in segments ?? [])
        {
            if (segment.Literal is string literal)
            {
                literalLength += literal.Length; // no concern about overflow as these were parsed out of a single string
            }
            else if (segment.ArgIndex >= 0)
            {
                formattedCount++;
                argsRequired = Math.Max(argsRequired, segment.ArgIndex + 1);
            }
        }

        // Store the derivative information.
        _argsRequired = argsRequired;
    }

    /// <summary>Parse the composite format string <paramref name="format"/>.</summary>
    /// <param name="format">The string to parse.</param>
    /// <returns>The parsed <see cref="CompositeFormat"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="format"/> is null.</exception>
    /// <exception cref="FormatException">A format item in <paramref name="format"/> is invalid.</exception>
    public static CompositeFormat Parse(string format)
    {
        var segments = new List<(string? Literal, int ArgIndex, int Alignment, string? Format)>();
        int failureOffset = default;
        if (!TryParseLiterals(format, segments, ref failureOffset))
        {
            // ThrowHelper.ThrowFormatInvalidString(failureOffset, failureReason);
            throw new FormatException($"Invalid character at {failureOffset}");
        }

        return new CompositeFormat(format, [.. segments]);
    }

    /// <summary>Gets the original composite format string used to create this <see cref="CompositeFormat"/> instance.</summary>
    public string Format { get; }

    /// <summary>Gets the minimum number of arguments that must be passed to a formatting operation using this <see cref="CompositeFormat"/>.</summary>
    /// <remarks>It's permissible to supply more arguments than this value, but it's an error to pass fewer.</remarks>
    public int MinimumArgumentCount => _argsRequired;

    /// <summary>Parse the composite format string into segments.</summary>
    /// <param name="format">The format string.</param>
    /// <param name="segments">The list into which to store the segments.</param>
    /// <param name="failureOffset">The offset at which a parsing error occured if <see langword="false"/> is returned.</param>
    /// <param name="failureReason">The reason for a parsing failure if <see langword="false"/> is returned.</param>
    /// <returns>true if the format string can be parsed successfully; otherwise, false.</returns>
    private static bool TryParseLiterals(
        ReadOnlySpan<char> format,
        List<(string? Literal, int ArgIndex, int Alignment, string? Format)> segments,
        ref int failureOffset
    )
    {
        // This parsing logic is copied from string.Format.  It's the same code modified to not format
        // as part of parsing and instead store the parsed literals and argument specifiers (alignment
        // and format) for later use.

        // Rather than parsing directly into the segments list, literals are parsed into a reusable builder.
        // Due to the nature of the parsing logic copied from string.Format, and our desire not to veer from
        // it significantly in order to maintain compatibility and accidental regression, multiple literals
        // next to each other might be parsed separately due to braces in between them.  This builder then
        // allows us to merge those segments back together easily prior to their being appended to the list.
        var vsb = new StringBuilder();

        // Repeatedly find the next hole and process it.
        var pos = 0;
        char ch;
        while (true)
        {
            // Skip until either the end of the input or the first unescaped opening brace, whichever comes first.
            // Along the way we need to also unescape escaped closing braces.
            while (true)
            {
                // Find the next brace.  If there isn't one, the remainder of the input is text to be appended, and we're done.
                var remainder = format.Slice(pos);
                var countUntilNextBrace = remainder.IndexOfAny('{', '}');
                if (countUntilNextBrace < 0)
                {
                    vsb.Append(remainder.ToString());
                    segments.Add((vsb.ToString(), -1, 0, null));
                    return true;
                }

                // Append the text until the brace.
                vsb.Append(remainder.Slice(0, countUntilNextBrace).ToString());
                pos += countUntilNextBrace;

                // Get the brace.  It must be followed by another character, either a copy of itself in the case of being
                // escaped, or an arbitrary character that's part of the hole in the case of an opening brace.
                var brace = format[pos];
                if (!TryMoveNext(format, ref pos, out ch))
                {
                    goto FailureUnclosedFormatItem;
                }
                if (brace == ch)
                {
                    vsb.Append(ch);
                    pos++;
                    continue;
                }

                // This wasn't an escape, so it must be an opening brace.
                if (brace != '{')
                {
                    goto FailureUnexpectedClosingBrace;
                }

                // Proceed to parse the hole.
                segments.Add((vsb.ToString(), -1, 0, null));
                vsb.Length = 0;
                break;
            }

            // We're now positioned just after the opening brace of an argument hole, which consists of
            // an opening brace, an index, an optional width preceded by a comma, and an optional format
            // preceded by a colon, with arbitrary amounts of spaces throughout.
            var width = 0;
            string? itemFormat = null; // used if itemFormat is null

            // First up is the index parameter, which is of the form:
            //     at least on digit
            //     optional any number of spaces
            // We've already read the first digit into ch.
            var index = ch - '0';
            if ((uint)index >= 10u)
            {
                goto FailureExpectedAsciiDigit;
            }

            // Common case is a single digit index followed by a closing brace.  If it's not a closing brace,
            // proceed to finish parsing the full hole format.
            if (!TryMoveNext(format, ref pos, out ch))
            {
                goto FailureUnclosedFormatItem;
            }
            if (ch != '}')
            {
                // Continue consuming optional additional digits.
                while (ch < 128 && char.IsDigit(ch))
                {
                    index = index * 10 + ch - '0';
                    if (!TryMoveNext(format, ref pos, out ch))
                    {
                        goto FailureUnclosedFormatItem;
                    }
                }

                // Consume optional whitespace.
                while (ch == ' ')
                {
                    if (!TryMoveNext(format, ref pos, out ch))
                    {
                        goto FailureUnclosedFormatItem;
                    }
                }

                // Parse the optional alignment, which is of the form:
                //     comma
                //     optional any number of spaces
                //     optional -
                //     at least one digit
                //     optional any number of spaces
                if (ch == ',')
                {
                    // Consume optional whitespace.
                    do
                    {
                        if (!TryMoveNext(format, ref pos, out ch))
                        {
                            goto FailureUnclosedFormatItem;
                        }
                    } while (ch == ' ');

                    // Consume an optional minus sign indicating left alignment.
                    var leftJustify = 1;
                    if (ch == '-')
                    {
                        leftJustify = -1;
                        if (!TryMoveNext(format, ref pos, out ch))
                        {
                            goto FailureUnclosedFormatItem;
                        }
                    }

                    // Parse alignment digits. The read character must be a digit.
                    width = ch - '0';
                    if ((uint)width >= 10u)
                    {
                        goto FailureExpectedAsciiDigit;
                    }
                    if (!TryMoveNext(format, ref pos, out ch))
                    {
                        goto FailureUnclosedFormatItem;
                    }
                    while (ch < 128 && char.IsDigit(ch))
                    {
                        width = width * 10 + ch - '0';
                        if (!TryMoveNext(format, ref pos, out ch))
                        {
                            goto FailureUnclosedFormatItem;
                        }
                    }
                    width *= leftJustify;

                    // Consume optional whitespace
                    while (ch == ' ')
                    {
                        if (!TryMoveNext(format, ref pos, out ch))
                        {
                            goto FailureUnclosedFormatItem;
                        }
                    }
                }

                // The next character needs to either be a closing brace for the end of the hole,
                // or a colon indicating the start of the format.
                if (ch != '}')
                {
                    if (ch != ':')
                    {
                        // Unexpected character
                        goto FailureUnclosedFormatItem;
                    }

                    // Search for the closing brace; everything in between is the format,
                    // but opening braces aren't allowed.
                    var startingPos = pos;
                    while (true)
                    {
                        if (!TryMoveNext(format, ref pos, out ch))
                        {
                            goto FailureUnclosedFormatItem;
                        }

                        if (ch == '}')
                        {
                            // Argument hole closed
                            break;
                        }

                        if (ch == '{')
                        {
                            // Braces inside the argument hole are not supported
                            goto FailureUnclosedFormatItem;
                        }
                    }

                    startingPos++;
                    itemFormat = format.Slice(startingPos, pos).ToString();
                }
            }

            pos++;

            segments.Add((null, index, width, itemFormat));

            // Continue parsing the rest of the format string.
        }

        FailureUnexpectedClosingBrace:
        failureOffset = pos;
        return false;

        FailureUnclosedFormatItem:
        failureOffset = pos;
        return false;

        FailureExpectedAsciiDigit:
        failureOffset = pos;
        return false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool TryMoveNext(ReadOnlySpan<char> format, ref int pos, out char nextChar)
        {
            pos++;
            if ((uint)pos >= (uint)format.Length)
            {
                nextChar = '\0';
                return false;
            }

            nextChar = format[pos];
            return true;
        }
    }
}
