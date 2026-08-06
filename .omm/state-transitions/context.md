Modes live on MainWindowViewModel (WorkspaceMode, IsPullRequestMode, IsHistoryMode). Async loads use CTS ownership and generation counters so superseded work cannot clear newer state.
