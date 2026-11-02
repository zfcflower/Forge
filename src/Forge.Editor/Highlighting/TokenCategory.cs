// Governed by THIRD_PARTY_NOTICES.md.

namespace Forge.Editor.Highlighting;

// The lexical categories a theme and an LSP server both speak, so semantic tokens can overlay lexical ones. The set is
// shared with LSP semantic token types through SemanticTokenMapper.
public enum TokenCategory : byte
{
    None = 0,
    Keyword,
    String,
    Comment,
    Number,
    Constant,
    Type,
    Function,
    Variable,
    Property,
    Parameter,
    Operator,
    Punctuation,
    Preprocessor,
    Tag,
    Attribute,
    Escape,
    Regexp,
    Modifier,
    Decorator,
    Label,
    Macro,
    Namespace,
    Class,
    Struct,
    Interface,
    Enum,
    EnumMember,
    Event,
    TypeParameter,
    Meta,
    Invalid,
}
