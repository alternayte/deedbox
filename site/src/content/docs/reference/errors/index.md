---
title: Error catalogue
description: Every DBX error code.
sidebar:
  order: 0
---

This page lists every Deedbox error. Each error message starts with its code and ends with a link to its page.

| Code | Error |
| --- | --- |
| [DBX001](/reference/errors/dbx001/) | The Deedbox schema is missing or older than this build |
| [DBX002](/reference/errors/dbx002/) | No database provider is configured |
| [DBX003](/reference/errors/dbx003/) | The schema name is not valid |
| [DBX004](/reference/errors/dbx004/) | A stream type or state type is registered twice |
| [DBX005](/reference/errors/dbx005/) | An event type or stored name is registered twice |
| [DBX006](/reference/errors/dbx006/) | An event type is not registered |
| [DBX007](/reference/errors/dbx007/) | A stored event type has no registered CLR type |
| [DBX008](/reference/errors/dbx008/) | A stream belongs to another stream type |
| [DBX009](/reference/errors/dbx009/) | A state type or stream type is not registered |
| [DBX010](/reference/errors/dbx010/) | One append holds events of several stream types |
| [DBX011](/reference/errors/dbx011/) | No JSON contract for a type |
| [DBX012](/reference/errors/dbx012/) | DbContexts do not share one connection |
| [DBX013](/reference/errors/dbx013/) | A database provider is configured twice |
| [DBX014](/reference/errors/dbx014/) | The stream is at another version |
| [DBX015](/reference/errors/dbx015/) | A stored name is not valid |
| [DBX016](/reference/errors/dbx016/) | Stored events have no mapping |
| [DBX017](/reference/errors/dbx017/) | Stored events are newer than this build |
| [DBX018](/reference/errors/dbx018/) | An event version has no upcaster |
| [DBX019](/reference/errors/dbx019/) | Stored events belong to another stream type |
| [DBX020](/reference/errors/dbx020/) | A projection or subscription is registered twice |
| [DBX021](/reference/errors/dbx021/) | A handler handles an unregistered event |
| [DBX022](/reference/errors/dbx022/) | The tenant ID is not valid |
| [DBX023](/reference/errors/dbx023/) | A projection cannot be rebuilt without ResetAsync |
| [DBX024](/reference/errors/dbx024/) | A batch projection is registered inline |
| [DBX025](/reference/errors/dbx025/) | Personal data needs a key mode |
| [DBX026](/reference/errors/dbx026/) | A personal-data property cannot hold null |
| [DBX027](/reference/errors/dbx027/) | A personal-data property has no subject |
| [DBX028](/reference/errors/dbx028/) | The stream was deleted |
| [DBX029](/reference/errors/dbx029/) | The master key cannot unwrap a key |
| [DBX030](/reference/errors/dbx030/) | Encrypted data or a key does not verify |
| [DBX031](/reference/errors/dbx031/) | A built-in event was appended or registered |
| [DBX032](/reference/errors/dbx032/) | A QueueBox publication is not valid |
| [DBX033](/reference/errors/dbx033/) | A projection or subscription name is not registered |
