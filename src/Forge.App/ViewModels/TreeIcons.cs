using System.Collections.Generic;
using System.Numerics;
using Avalonia.Media;

namespace Forge.App.ViewModels;

/// <summary>
/// The tree icons, as geometry so the shell ships no icon package and no font dependency. Every glyph is the 24 px
/// regular variant of the same Fluent System Icon the chrome uses (see <c>Resources/Icons.axaml</c>); the view paints it
/// with the built-in <c>PathIcon</c>, which fills with <c>Foreground</c>. Geometries are thread-affine Avalonia objects,
/// so they are cached per thread: each UI thread (and each headless session) gets its own set.
/// </summary>
internal static class TreeIcons
{
    [ThreadStatic]
    private static TreeIconSet? set;

    private static TreeIconSet Set => set ??= new TreeIconSet();

    public static Geometry Chevron => Set.Chevron;

    /// <summary>Closed folder: a tabbed rectangle outline.</summary>
    public static Geometry Folder => Set.Folder;

    /// <summary>Open folder: the front flap is folded forward.</summary>
    public static Geometry FolderOpen => Set.FolderOpen;

    /// <summary>File: a sheet outline with its top-right corner folded.</summary>
    public static Geometry File => Set.File;

    public static Geometry General => Project;

    public static Geometry Project => Set.Project;

    /// <summary>
    /// The project row's trailing button ("new workspace", a worktree on a new branch): Fluent <c>organization_add</c> in its
    /// filled variant, which is what "the same size as the name, and bold" asks for without a second weight system.
    /// </summary>
    public static Geometry OrganizationAdd => Set.OrganizationAdd;

    /// <summary>
    /// The trailing button of every row that starts a conversation (a branch's "new session", the general area's "new chat"):
    /// Fluent <c>chat_add</c>, one plus inside the conversation it opens.
    /// </summary>
    public static Geometry ChatAdd => Set.ChatAdd;

    /// <summary>
    /// An agent session (a session the user opened under a root, standing beside it): Fluent <c>comment_mention</c>, the one
    /// conversation glyph that says "a session you address from another one".
    /// </summary>
    public static Geometry CommentMention => Set.CommentMention;

    /// <summary>A sub-agent that may still delegate: Fluent <c>chat_arrow_back_down</c>.</summary>
    public static Geometry ChatArrowBackDown => Set.ChatArrowBackDown;

    /// <summary>The last agent level (<c>MaxSubAgentDepth</c>): a sub-agent that cannot spawn another one.</summary>
    public static Geometry ChatLock => Set.ChatLock;

    public static Geometry Worktree => Set.Worktree;

    /// <summary>
    /// The repository root checkout: Fluent <c>home_garage</c>, a house with a garage door. A worktree row wears the branch
    /// glyph, so the project's own checkout needs a shape of its own rather than a second worktree branch.
    /// </summary>
    public static Geometry MainCheckout => Set.MainCheckout;

    public static Geometry History => Set.History;

    public static Geometry Session => Set.Session;

    public static Geometry Bubble => Set.Bubble;

    private sealed class TreeIconSet
    {
        /// <summary>展开箭头:与 <c>TreeChevronGeometry</c> 同一枚 Fluent chevron_right;展开时由视图把整个图标转 90°。</summary>
        public Geometry Chevron { get; } = Parse("M8.47 4.22c-.3.3-.3.77 0 1.06L15.19 12l-6.72 6.72a.75.75 0 1 0 1.06 1.06l7.25-7.25c.3-.3.3-.77 0-1.06L9.53 4.22a.75.75 0 0 0-1.06 0");

        public Geometry Folder { get; } = Parse("M3.5 6.25V8h4.63q.31 0 .53-.22l1.53-1.53-1.53-1.53a.8.8 0 0 0-.53-.22H5.25c-.97 0-1.75.78-1.75 1.75m-1.5 0C2 4.45 3.46 3 5.25 3h2.88c.6 0 1.17.24 1.59.66l1.84 1.84h7.19c1.8 0 3.25 1.46 3.25 3.25v9c0 1.8-1.46 3.25-3.25 3.25H5.25A3.25 3.25 0 0 1 2 17.75zM3.5 9.5v8.25c0 .97.78 1.75 1.75 1.75h13.5c.97 0 1.75-.78 1.75-1.75v-9c0-.97-.78-1.75-1.75-1.75h-7.19L9.72 8.84c-.42.42-1 .66-1.6.66z");

        public Geometry FolderOpen { get; } = Parse("M3.5 6.25c0-.97.78-1.75 1.75-1.75h2.88q.31 0 .53.22l2.06 2.06q.22.22.53.22h5.5c.97 0 1.75.78 1.75 1.75q0 .13.04.25H8.72c-1.34 0-2.58.71-3.25 1.87L3.5 14.28zM2 17.79A3.25 3.25 0 0 0 5.25 21h11.04c1.33 0 2.57-.72 3.24-1.88l3.03-5.25A3.25 3.25 0 0 0 19.96 9a1 1 0 0 0 .04-.25c0-1.8-1.45-3.25-3.25-3.25h-5.19L9.72 3.66c-.42-.42-1-.66-1.6-.66H5.26A3.25 3.25 0 0 0 2 6.25v11.54m6.72-7.3h11.03a1.75 1.75 0 0 1 1.51 2.63l-3.03 5.25c-.4.7-1.14 1.13-1.95 1.13H5.25a1.75 1.75 0 0 1-1.51-2.63l3.03-5.25c.4-.7 1.14-1.12 1.95-1.12");

        public Geometry File { get; } = Parse("M6 2a2 2 0 0 0-2 2v16c0 1.1.9 2 2 2h12a2 2 0 0 0 2-2V9.83a2 2 0 0 0-.59-1.42L13.6 2.6A2 2 0 0 0 12.17 2zm-.5 2c0-.28.22-.5.5-.5h6V8c0 1.1.9 2 2 2h4.5v10a.5.5 0 0 1-.5.5H6a.5.5 0 0 1-.5-.5zm11.88 4.5H14a.5.5 0 0 1-.5-.5V4.62z");

        public Geometry General => Project;

        /// <summary>
        /// The one container glyph of the tree, worn by both the general section and every project: an open box. It reads as
        /// "an area that holds conversations" without repeating the file tree's folder, which in this panel would say
        /// "a directory" instead.
        /// </summary>
        public Geometry Project { get; } = Parse("M6.05 7.98a.75.75 0 0 1 .97-.43L12 9.44l4.98-1.9a.75.75 0 1 1 .54 1.41l-4.77 1.8v5.5a.75.75 0 1 1-1.5 0v-5.5l-4.77-1.8a.75.75 0 0 1-.43-.97m4.54-5.47c.9-.37 1.92-.37 2.82 0l7.5 3.04c.66.27 1.09.91 1.09 1.62v9.66c0 .7-.43 1.35-1.1 1.62l-7.5 3.04c-.9.36-1.9.36-2.8 0l-7.5-3.04c-.67-.27-1.1-.91-1.1-1.62V7.17c0-.71.43-1.35 1.1-1.62zm2.26 1.4a2.3 2.3 0 0 0-1.7 0l-7.5 3.03a.3.3 0 0 0-.15.23v9.66q0 .16.16.23l7.5 3.04c.54.22 1.14.22 1.69 0l7.5-3.04q.14-.07.15-.23V7.17q0-.17-.16-.23z");

        /// <summary>
        /// Fluent <c>organization_add</c> (24 regular): the project row's trailing "new workspace" button. Regular, not filled:
        /// at 16 px the filled variant read as a second, heavier title beside the project name.
        /// </summary>
        public Geometry OrganizationAdd { get; } = Parse("M17.5 12C20.5376 12 23 14.4624 23 17.5C23 20.5376 20.5376 23 17.5 23C14.4624 23 12 20.5376 12 17.5C12 14.4624 14.4624 12 17.5 12ZM11.75 1.99707C13.8223 1.99715 15.502 3.67772 15.502 5.75C15.5019 7.56533 14.2128 9.07919 12.5 9.42676V11.5H14.9971C14.167 11.8467 13.425 12.361 12.8115 13H7.75C7.33579 13 7 13.3358 7 13.75V14.5742C8.71404 14.9208 10.0048 16.4357 10.0049 18.252C10.0049 20.3243 8.32428 22.0039 6.25195 22.0039C4.17978 22.0037 2.5 20.3242 2.5 18.252C2.50007 16.4373 3.78823 14.9236 5.5 14.5752V13.75C5.5 12.5074 6.50736 11.5 7.75 11.5H11V9.42676C9.28706 9.07931 7.99812 7.56544 7.99805 5.75C7.99805 3.67767 9.67767 1.99707 11.75 1.99707ZM17.5 13.999C17.2239 13.999 17.0001 14.223 17 14.499V17H14.4951C14.2195 17.0003 13.9961 17.224 13.9961 17.5C13.9961 17.776 14.2195 17.9997 14.4951 18H17.001V20.5039C17.0012 20.7798 17.225 21.0038 17.501 21.0039C17.777 21.0039 18.0008 20.7799 18.001 20.5039V18H20.4971C20.7727 17.9997 20.9961 17.776 20.9961 17.5C20.9961 17.224 20.7727 17.0003 20.4971 17H18V14.499C17.9999 14.223 17.7761 13.999 17.5 13.999ZM6.25195 16C5.00825 16.0002 4.00008 17.0082 4 18.252C4 19.4957 5.0082 20.5037 6.25195 20.5039C7.49586 20.5039 8.50488 19.4959 8.50488 18.252C8.5048 17.0081 7.49581 16 6.25195 16ZM11.75 3.49805C10.5061 3.49805 9.49805 4.5061 9.49805 5.75C9.49813 6.99383 10.5061 8.00195 11.75 8.00195C12.9938 8.00187 14.0019 6.99378 14.002 5.75C14.002 4.50615 12.9938 3.49813 11.75 3.49805Z");

        /// <summary>Fluent <c>chat_add</c>: the trailing button of a row whose button starts a conversation.</summary>
        public Geometry ChatAdd { get; } = Parse("M12.75 7.75C12.75 7.33579 12.4142 7 12 7C11.5858 7 11.25 7.33579 11.25 7.75V11.25H7.75C7.33579 11.25 7 11.5858 7 12C7 12.4142 7.33579 12.75 7.75 12.75H11.25V16.25C11.25 16.6642 11.5858 17 12 17C12.4142 17 12.75 16.6642 12.75 16.25V12.75H16.25C16.6642 12.75 17 12.4142 17 12C17 11.5858 16.6642 11.25 16.25 11.25H12.75V7.75ZM22 12C22 6.47715 17.5228 2 12 2C6.47715 2 2 6.47715 2 12C2 13.6203 2.38637 15.186 3.11461 16.5922L2.04695 20.4151C1.98549 20.6349 1.98546 20.8676 2.04691 21.0876C2.23258 21.7525 2.92212 22.141 3.58704 21.9553L7.41286 20.888C8.81782 21.6146 10.3817 22 12 22C17.5228 22 22 17.5228 22 12ZM3.5 12C3.5 7.30558 7.30558 3.5 12 3.5C16.6944 3.5 20.5 7.30558 20.5 12C20.5 16.6944 16.6944 20.5 12 20.5C10.5322 20.5 9.12006 20.1281 7.86709 19.4295L7.59755 19.2792L3.61096 20.3914L4.72368 16.4072L4.57303 16.1375C3.87277 14.8834 3.5 13.4696 3.5 12Z");

        /// <summary>Fluent <c>comment_mention</c>: an agent session's row.</summary>
        public Geometry CommentMention { get; } = Parse("M22 14.7505V10.4728C21.5548 10.8712 21.0498 11.2042 20.5 11.4565V14.7505C20.5 15.717 19.7165 16.5005 18.75 16.5005H12.5135L7.49986 20.2511L7.49879 16.5005H5.25C4.2835 16.5005 3.5 15.717 3.5 14.7505V6.25055C3.5 5.28405 4.2835 4.50055 5.25 4.50055H12.189C12.326 3.96839 12.5341 3.46481 12.8027 3.00055H5.25C3.45507 3.00055 2 4.45562 2 6.25055V14.7505C2 16.5455 3.45507 18.0005 5.25 18.0005H5.99921L6 20.7505C6 21.0201 6.08736 21.2828 6.24901 21.4989C6.6625 22.0518 7.44585 22.1647 7.99868 21.7512L13.0125 18.0005H18.75C20.5449 18.0005 22 16.5455 22 14.7505ZM18 2C15.7906 2 13.9995 3.7911 13.9995 6.00054C13.9995 8.20997 15.7906 10.0011 18 10.0011C18.8264 10.0011 19.5878 9.75982 20.2217 9.33514C20.4512 9.18145 20.7617 9.24285 20.9154 9.47228C21.0691 9.7017 21.0077 10.0123 20.7783 10.166C19.9798 10.7008 19.0248 11.0011 18 11.0011C15.2383 11.0011 12.9995 8.76226 12.9995 6.00054C12.9995 3.23882 15.2383 1 18 1C20.7618 1 23.0006 3.23882 23.0006 6.00054L23.0005 6.01035V6.75052C23.0005 7.71702 22.217 8.50052 21.2505 8.50052C20.6484 8.50052 20.1173 8.19645 19.8024 7.73348C19.3476 8.20632 18.7084 8.50055 18.0005 8.50055C16.6198 8.50055 15.5005 7.38126 15.5005 6.00055C15.5005 4.61984 16.6198 3.50055 18.0005 3.50055C18.5633 3.50055 19.0827 3.68652 19.5005 4.00037C19.5006 3.72431 19.7244 3.50055 20.0005 3.50055C20.2766 3.50055 20.5005 3.72441 20.5005 4.00055V6.75052C20.5005 7.16474 20.8363 7.50052 21.2505 7.50052C21.6647 7.50052 22.0005 7.16474 22.0005 6.75052V5.9952L22.0006 5.98669C21.9931 3.78362 20.2049 2 18 2ZM16.5005 6.00055C16.5005 6.82898 17.1721 7.50055 18.0005 7.50055C18.8289 7.50055 19.5005 6.82898 19.5005 6.00055C19.5005 5.17212 18.8289 4.50055 18.0005 4.50055C17.1721 4.50055 16.5005 5.17212 16.5005 6.00055Z");

        /// <summary>Fluent <c>chat_arrow_back_down</c>: a sub-agent that may still delegate (the same glyph the transcript follower uses).</summary>
        public Geometry ChatArrowBackDown { get; } = Parse("M12 2C17.5228 2 22 6.47715 22 12C22 12.2628 21.9899 12.5232 21.97 12.7809C21.5319 12.3658 21.0361 12.0111 20.4958 11.73C20.3532 7.16054 16.6041 3.5 12 3.5C7.30558 3.5 3.5 7.30558 3.5 12C3.5 13.4696 3.87277 14.8834 4.57303 16.1375L4.72368 16.4072L3.61096 20.3914L7.59755 19.2792L7.86709 19.4295C9.04305 20.0852 10.3592 20.4531 11.73 20.4958C12.0111 21.0361 12.3658 21.5319 12.7809 21.97C12.5232 21.9899 12.2628 22 12 22C10.3817 22 8.81782 21.6146 7.41286 20.888L3.58704 21.9553C2.92212 22.141 2.23258 21.7525 2.04691 21.0876C1.98546 20.8676 1.98549 20.6349 2.04695 20.4151L3.11461 16.5922C2.38637 15.186 2 13.6203 2 12C2 6.47715 6.47715 2 12 2ZM23 17.5C23 14.4624 20.5376 12 17.5 12C14.4624 12 12 14.4624 12 17.5C12 20.5376 14.4624 23 17.5 23C20.5376 23 23 20.5376 23 17.5ZM16.3536 19.6464C16.5488 19.8417 16.5488 20.1583 16.3536 20.3536C16.1583 20.5488 15.8417 20.5488 15.6464 20.3536L14.1464 18.8536C13.9512 18.6583 13.9512 18.3417 14.1464 18.1464L15.6464 16.6464C15.8417 16.4512 16.1583 16.4512 16.3536 16.6464C16.5488 16.8417 16.5488 17.1583 16.3536 17.3536L15.7071 18H17.75C18.9926 18 20 16.9926 20 15.75V15.5C20 15.2239 20.2239 15 20.5 15C20.7761 15 21 15.2239 21 15.5V15.75C21 17.5449 19.5449 19 17.75 19H15.7071L16.3536 19.6464Z");

        /// <summary>Fluent <c>chat_lock</c>: the last agent level, which cannot spawn another agent.</summary>
        public Geometry ChatLock { get; } = Parse("M22 12C22 6.47715 17.5228 2 12 2C6.47715 2 2 6.47715 2 12C2 13.6203 2.38637 15.186 3.11461 16.5922L2.04695 20.4151C1.98549 20.6349 1.98546 20.8676 2.04691 21.0876C2.23258 21.7525 2.92212 22.141 3.58704 21.9553L7.41286 20.888C8.81782 21.6146 10.3817 22 12 22C12.3511 22 12.698 21.9819 13.0398 21.9466C13.0136 21.8017 13 21.6524 13 21.5V20.4418C12.672 20.4802 12.3383 20.5 12 20.5C10.5322 20.5 9.12006 20.1281 7.86709 19.4295L7.59755 19.2792L3.61096 20.3914L4.72368 16.4072L4.57303 16.1375C3.87277 14.8834 3.5 13.4696 3.5 12C3.5 7.30558 7.30558 3.5 12 3.5C16.3886 3.5 20.0004 6.82592 20.4524 11.0947C21.1898 11.5913 21.728 12.3612 21.9215 13.2593C21.9733 12.8468 22 12.4265 22 12ZM16 15H15.5C14.6716 15 14 15.6716 14 16.5V21.5C14 22.3284 14.6716 23 15.5 23H21.5C22.3284 23 23 22.3284 23 21.5V16.5C23 15.6716 22.3284 15 21.5 15H21V14C21 12.6193 19.8807 11.5 18.5 11.5C17.1193 11.5 16 12.6193 16 14V15ZM17.5 14C17.5 13.4477 17.9477 13 18.5 13C19.0523 13 19.5 13.4477 19.5 14V15H17.5V14ZM19.5 19C19.5 19.5523 19.0523 20 18.5 20C17.9477 20 17.5 19.5523 17.5 19C17.5 18.4477 17.9477 18 18.5 18C19.0523 18 19.5 18.4477 19.5 19Z");

        public Geometry Worktree { get; } = Parse("M11 5.5a3.5 3.5 0 0 1-3 3.46v2.54h6.25c1.24 0 2.25-1 2.25-2.25v-.4a3.5 3.5 0 1 1 1.5.11v.29A3.75 3.75 0 0 1 14.25 13H8v2.04a3.5 3.5 0 1 1-1.5.1V8.86A3.5 3.5 0 1 1 11 5.5m-3.5 2a2 2 0 1 0 0-4 2 2 0 0 0 0 4m10 0a2 2 0 1 0 0-4 2 2 0 0 0 0 4m-8 11a2 2 0 1 0-4 0 2 2 0 0 0 4 0");

        /// <summary>The repository root checkout: the same Fluent <c>home_garage</c> shape the icon table carries.</summary>
        public Geometry MainCheckout { get; } = Parse("M11.51 3.68 4.76 9.4a.8.8 0 0 0-.26.57v9.27c0 .14.11.25.25.25H7v-5.25c0-.97.78-1.75 1.75-1.75h6.5c.97 0 1.75.78 1.75 1.75v5.25h2.25q.23-.02.25-.25V9.98q-.01-.35-.26-.57l-6.75-5.73a.75.75 0 0 0-.98 0M8.5 17.5v2h7v-2zm7-1.5v-1.75a.25.25 0 0 0-.25-.25h-6.5a.25.25 0 0 0-.25.25V16zM4.75 21C3.78 21 3 20.22 3 19.25V9.98c0-.66.29-1.3.8-1.72l6.74-5.73a2.25 2.25 0 0 1 2.92 0l6.75 5.73c.5.43.79 1.06.79 1.72v9.27c0 .97-.78 1.75-1.75 1.75z");

        public Geometry History { get; } = Parse("M12 4.5a7.5 7.5 0 1 1-7.42 6.4c.07-.46-.26-.9-.72-.9-.37 0-.7.26-.76.62Q3 11.3 3 12a9 9 0 1 0 3-6.7V4.24a.75.75 0 0 0-1.5 0v3c0 .41.34.75.75.75h3a.75.75 0 0 0 0-1.5H6.9a7.5 7.5 0 0 1 5.1-2m.5 3.25a.75.75 0 0 0-1.5 0v4.5c0 .41.34.75.75.75h3a.75.75 0 0 0 0-1.5H12.5z");

        public Geometry Session => Bubble;

        /// <summary>
        /// The one conversation glyph of the tree — Fluent <c>chat_sparkle</c>, the same one the tab strip's <c>IconChat</c> wears: a session row is a
        /// conversation. The active session's row is told apart by colour, not by a second, filled variant.
        /// </summary>
        public Geometry Bubble { get; } = Parse("M16.09 6.41a3 3 0 0 0-1.35-.95L13.36 5a.54.54 0 0 1 0-1.03l1.38-.44a2.8 2.8 0 0 0 1.76-1.77l.01-.03.45-1.38a.54.54 0 0 1 1.03 0l.44 1.38a2.8 2.8 0 0 0 1.8 1.8l1.38.44.03.01a.54.54 0 0 1 0 1.03l-1.38.44a2.8 2.8 0 0 0-1.8 1.8l-.45 1.38-.01.03a.54.54 0 0 1-1.01-.03l-.45-1.38q-.15-.46-.45-.85m7.7 3.8-.77-.25a1.6 1.6 0 0 1-1-1l-.25-.76a.3.3 0 0 0-.57 0l-.25.77a1.6 1.6 0 0 1-.98 1l-.77.24a.3.3 0 0 0 0 .57l.77.25a1.6 1.6 0 0 1 1 1l.24.77a.3.3 0 0 0 .58 0l.24-.77a1.6 1.6 0 0 1 1-1l.77-.24a.3.3 0 0 0 0-.57zM12 2a10 10 0 0 1 2.76.39q-.18.1-.37.17L13.05 3q-.43.16-.72.5H12a8.5 8.5 0 0 0-7.43 12.64l.15.27-1.1 3.98 3.98-1.11.27.15a8.5 8.5 0 0 0 12.5-5.96q.14.2.34.34a1.3 1.3 0 0 0 1.09.2A10 10 0 0 1 7.4 20.88L3.6 21.96a1.25 1.25 0 0 1-1.54-1.54l1.06-3.83A10 10 0 0 1 12 2");

        private static Geometry Parse(string path) => StreamGeometry.Parse(path);
    }
}

/// <summary>Global motion gate for looping animations - the tree's running-agent breathing and the chat timeline's running
/// node. Avalonia 12 exposes no OS reduced-motion preference and this module forbids P/Invoke, so the shell sets this from
/// the appearance preference (<see cref="Forge.App.Appearance.AppearanceApplier"/>).</summary>
internal static class TreeMotion
{
    private static bool reducedMotion;

    /// <summary>True when looping tree animations (the running agent's breathing) must not play.</summary>
    public static bool ReducedMotion
    {
        get => reducedMotion;
        set
        {
            if (reducedMotion == value)
            {
                return;
            }

            reducedMotion = value;
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Raised on the UI thread when <see cref="ReducedMotion"/> changed, so a live looping animation (a running timeline
    /// node) can stop at once instead of on its next unrelated notification.
    /// </summary>
    public static event EventHandler? Changed;
}

/// <summary>One indent step of a tree row; <see cref="DrawLine"/> is the 1 px guide line of a tree, not of an agent list,
/// and <see cref="Step"/> is the cell width the view gives this mark.</summary>
internal sealed record IndentMark(int Level, bool DrawLine, int Step);

/// <summary>Shared indent lists, so a row references one cached array instead of allocating its own per render.</summary>
internal static class IndentMarks
{
    /// <summary>Width of the chevron column; the chevron box fills it, so a child chevron lines up under its parent's icon.</summary>
    public const int ChevronSlot = 10;

    /// <summary>The panel padding every row is inset by, so a row does not start against the panel edge.</summary>
    public const int RowInset = 6;

    /// <summary>Gap between the chevron column and the icon; the row templates use the same value as the icon's left margin.</summary>
    public const int IconGap = 6;

    /// <summary>One indent step for folder/file rows: the chevron slot plus the chevron-to-icon gap.</summary>
    public const int Step = ChevronSlot + IconGap;

    /// <summary>
    /// The whole leading width of a row before its icon: the panel inset, the chevron column (which an agent row leaves
    /// empty) and the icon gap. Both row templates indent by this plus <see cref="Step"/> per level, so an agent row lands
    /// under the row it belongs to instead of level with it.
    /// </summary>
    public const int IconIndent = RowInset + ChevronSlot + IconGap;

    /// <summary>One indent step for agent rows, which keep the previous 16 px rhythm and draw no guide lines.</summary>
    public const int SpacerStep = 16;

    private const int MaxDepth = 32;

    private static readonly IReadOnlyList<IndentMark>[] Lines = Build(drawLine: true, Step);
    private static readonly IReadOnlyList<IndentMark>[] PlainSteps = Build(drawLine: false, Step);
    private static readonly IReadOnlyList<IndentMark>[] Spacers = Build(drawLine: false, SpacerStep);
    private static readonly Dictionary<int, IReadOnlyList<IndentMark>> Masks = [];

    /// <summary>Indent steps joined by a vertical guide line, for a fully-expanded chain.</summary>
    public static IReadOnlyList<IndentMark> For(int depth) => At(Lines, depth);

    /// <summary>Indent steps without a guide line, for the agent tree.</summary>
    public static IReadOnlyList<IndentMark> Spacer(int depth) => At(Spacers, depth);

    /// <summary>
    /// Indent steps of the folder width that draw no line: the project tree keeps the depth rhythm but no longer threads a
    /// vertical line through it, since a hairline between rows read as a second, competing column.
    /// </summary>
    public static IReadOnlyList<IndentMark> Plain(int depth) => At(PlainSteps, depth);

    /// <summary>
    /// Marks for a row whose expanded ancestors occupy the levels in <paramref name="mask"/> (bit L = level L). The result
    /// is cached so template bindings never allocate a new collection.
    /// </summary>
    public static IReadOnlyList<IndentMark> ForMask(int mask)
    {
        if (mask == 0)
        {
            return [];
        }

        if (!Masks.TryGetValue(mask, out var marks))
        {
            marks = BuildMask(mask);
            Masks[mask] = marks;
        }

        return marks;
    }

    private static IReadOnlyList<IndentMark> BuildMask(int mask)
    {
        var count = BitOperations.PopCount((uint)mask);

        // A fully-expanded chain is the common case: reuse the shared per-depth array.
        if (count < MaxDepth && mask == (1 << count) - 1)
        {
            return At(Lines, count);
        }

        var marks = new IndentMark[count];
        var index = 0;
        for (var level = 0; level < MaxDepth && index < count; level++)
        {
            if ((mask & (1 << level)) != 0)
            {
                marks[index++] = new IndentMark(level, true, Step);
            }
        }

        return marks;
    }

    private static IReadOnlyList<IndentMark> At(IReadOnlyList<IndentMark>[] cache, int depth) =>
        depth <= 0 ? [] : cache[depth < MaxDepth ? depth : MaxDepth - 1];

    private static IReadOnlyList<IndentMark>[] Build(bool drawLine, int step)
    {
        var cache = new IReadOnlyList<IndentMark>[MaxDepth];
        cache[0] = [];
        for (var depth = 1; depth < MaxDepth; depth++)
        {
            var marks = new IndentMark[depth];
            for (var level = 0; level < depth; level++)
            {
                marks[level] = new IndentMark(level, drawLine, step);
            }

            cache[depth] = marks;
        }

        return cache;
    }
}
