// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Xunit;

// The core registries (hooks, features, lifecycle) are process-wide statics.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
