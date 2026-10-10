// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Xunit;

// NetCapture and NetActive are static: tests that use them can't run alongside each other.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
