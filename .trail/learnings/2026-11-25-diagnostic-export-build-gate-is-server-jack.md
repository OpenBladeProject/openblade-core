---
title: Diagnostic export build gate is server-owned
author: Jack
date: 2026-11-25
branch: feat/native-export-diagnostics
---

The export single-build gate and its unfinished-build flag live in Subscriptions (cfg export), not a static, so each server owns one. A test that checks one-at-a-time or the timed-out-build refusal must share ONE Rc<Subscriptions> across its connections; exchange() makes a fresh one, exchange_sharing() takes it. A build past its 20 s bound cannot be interrupted, so the next export is refused with the time-bound failure until it ends rather than started beside it. The thermal lane records its private-projection samples in diagnostic-export-native-private.json; the shared native.json is identical in every lane, because the projection is a no-op unless a private owner is bound. The managed codec rejects hyperBoostEntryRestriction without hyperBoostAccess.
