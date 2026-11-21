---
okf_version: "0.3"
title: Medal Sync knowledge base
description: Architecture, systems, runbooks and policies for the Medal Sync codebase. Use it before changing session, ownership, storage, release or repository-hygiene behaviour.
license: MIT
---

# Architecture

* [Session lifecycle](architecture/session-lifecycle.md) - How one Medal session moves from launch through supervision, idle sync and finish until the library is Ready for another PC.
* [Library ownership and generations](architecture/library-ownership.md) - The one-PC-at-a-time ownership journal, immutable library generations and handoff requests that let PCs share one Medal library safely.

# Systems

* [Storage and replication](systems/storage-and-replication.md) - Storage modes, the rclone and WinFsp clip drive, settled-media and disk-space rules, and how copies to primary storage and cloud replicas are verified.

# Runbooks

* [Release and versioning](runbooks/release-and-versioning.md) - Steps to land a change on dev, publish betas, prepare a release and promote dev to main, plus the version numbering rules the checks enforce.

# Policies

* [Public source hygiene](policies/public-source-hygiene.md) - What may be committed to this public repository, how the source inventory classifies files for CI, and the documentation link check.

# Subdirectories

* [architecture](architecture/index.md) - How sessions and shared library ownership work.
* [systems](systems/index.md) - Storage, the clip drive and replication.
* [runbooks](runbooks/index.md) - Step-by-step maintainer procedures.
* [policies](policies/index.md) - Rules the repository checks enforce.

# History

* [Change log](log.md) - Additions and updates to this knowledge base, newest first.
