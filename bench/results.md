# Deedbox benchmarks

Append throughput and latency across the matrix: writers × events per append × provider × mode. Each cell runs 2 s of warm-up, then 8 s of measurement. Each writer appends to its own stream, so the position counter is the only point of contention. Counter p50 and p99 are the time from the counter update, including any wait for its lock, to the end of the append.

- Run: the nightly workflow of 2026-10-19, GitHub-hosted `ubuntu-latest` runner, Postgres 17 and SQL Server 2022 in Docker on the same machine: <https://github.com/alternayte/deedbox/actions/runs/39224756475>.
- Commit: `0a704c0`, which holds the 0.8.0 code.
- Modes: `neither` means Deedbox opens and commits the transaction; `efcore` means `UseDbContext` with one inline EF Core projection.

| Provider | Mode | Writers | Events per append | Appends/s | Events/s | p50 ms | p99 ms | Counter p50 ms | Counter p99 ms |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| postgres | neither | 1 | 1 | 655 | 655 | 1.52 | 1.92 | 0.84 | 1.08 |
| postgres | neither | 8 | 1 | 1142 | 1142 | 5.44 | 24.72 | 4.56 | 23.82 |
| postgres | neither | 32 | 1 | 1006 | 1006 | 20.75 | 155.61 | 19.93 | 154.60 |
| postgres | neither | 128 | 1 | 744 | 744 | 115.63 | 767.57 | 114.48 | 766.67 |
| postgres | neither | 1 | 10 | 618 | 6178 | 1.61 | 2.01 | 0.92 | 1.16 |
| postgres | neither | 8 | 10 | 1003 | 10029 | 5.65 | 31.15 | 4.77 | 30.31 |
| postgres | neither | 32 | 10 | 878 | 8784 | 21.67 | 191.96 | 20.68 | 190.98 |
| postgres | neither | 128 | 10 | 659 | 6590 | 131.91 | 921.73 | 130.85 | 920.52 |
| postgres | efcore | 1 | 1 | 326 | 326 | 3.02 | 3.90 | 0.88 | 1.16 |
| postgres | efcore | 8 | 1 | 823 | 823 | 9.03 | 21.29 | 5.10 | 17.43 |
| postgres | efcore | 32 | 1 | 713 | 713 | 29.35 | 212.64 | 25.41 | 207.91 |
| postgres | efcore | 128 | 1 | 536 | 536 | 160.46 | 1133.37 | 156.68 | 1128.74 |
| postgres | efcore | 1 | 10 | 296 | 2957 | 3.35 | 4.21 | 1.04 | 1.30 |
| postgres | efcore | 8 | 10 | 725 | 7254 | 10.26 | 23.78 | 5.69 | 19.13 |
| postgres | efcore | 32 | 10 | 648 | 6484 | 31.69 | 237.51 | 27.24 | 233.01 |
| postgres | efcore | 128 | 10 | 518 | 5180 | 167.19 | 1116.63 | 162.23 | 1112.98 |
| sqlserver | neither | 1 | 1 | 471 | 471 | 2.08 | 2.80 | 1.17 | 1.63 |
| sqlserver | neither | 8 | 1 | 820 | 820 | 8.73 | 56.52 | 7.67 | 55.45 |
| sqlserver | neither | 32 | 1 | 872 | 872 | 36.38 | 41.63 | 35.29 | 40.59 |
| sqlserver | neither | 128 | 1 | 762 | 762 | 118.40 | 371.06 | 117.02 | 366.84 |
| sqlserver | neither | 1 | 10 | 407 | 4074 | 2.34 | 4.28 | 1.38 | 2.83 |
| sqlserver | neither | 8 | 10 | 662 | 6616 | 10.62 | 72.07 | 9.53 | 70.92 |
| sqlserver | neither | 32 | 10 | 667 | 6675 | 45.48 | 75.83 | 44.24 | 74.62 |
| sqlserver | neither | 128 | 10 | 613 | 6129 | 110.08 | 356.87 | 108.36 | 339.31 |
| sqlserver | efcore | 1 | 1 | 245 | 245 | 4.00 | 9.59 | 1.25 | 1.60 |
| sqlserver | efcore | 8 | 1 | 660 | 660 | 11.93 | 15.33 | 7.86 | 10.62 |
| sqlserver | efcore | 32 | 1 | 600 | 600 | 51.76 | 81.43 | 47.26 | 76.36 |
| sqlserver | efcore | 128 | 1 | 582 | 582 | 122.80 | 352.79 | 118.18 | 345.71 |
| sqlserver | efcore | 1 | 10 | 235 | 2347 | 4.17 | 6.79 | 1.41 | 2.05 |
| sqlserver | efcore | 8 | 10 | 574 | 5744 | 13.56 | 22.27 | 9.45 | 17.74 |
| sqlserver | efcore | 32 | 10 | 525 | 5253 | 58.41 | 116.19 | 53.94 | 113.61 |
| sqlserver | efcore | 128 | 10 | 502 | 5016 | 127.80 | 2067.20 | 121.69 | 231.34 |

## Reading the numbers

- Appends serialize on the position counter from its update to commit, so throughput peaks at a few writers and falls slowly as more writers queue.
- More events per append cost little: 10 events per append move about 10 times the events at a similar append rate.
- With many writers, latency is almost all counter wait: counter p50 is close to append p50.
- One table is one run of one runner. Read the cells as an order of magnitude, not as a figure to quote.

## The spread between runs

A GitHub-hosted runner gives a different answer every night, for the same code. Seven nightly runs measure identical benchmarked code: `src` and `bench` do not change from commit `0a704c0` to commit `168d306`. These are their rates for the peak Postgres cell, 8 writers and 1 event per append, and for the SQL Server single-writer cell:

| Run | Commit | Postgres 8×1 appends/s | SQL Server 1×1 appends/s |
| --- | --- | ---: | ---: |
| [2026-10-17](https://github.com/alternayte/deedbox/actions/runs/39014541873) | `0a704c0` | 846 | 395 |
| [2026-10-18](https://github.com/alternayte/deedbox/actions/runs/39109528885) | `0a704c0` | 1345 | 670 |
| [2026-10-19](https://github.com/alternayte/deedbox/actions/runs/39224756475) | `0a704c0` | 1142 | 471 |
| [2026-10-20](https://github.com/alternayte/deedbox/actions/runs/39393101278) | `0a704c0` | 1097 | 250 |
| [2026-10-21](https://github.com/alternayte/deedbox/actions/runs/39521110453) | `7a006bd` | 853 | 418 |
| [2026-10-21](https://github.com/alternayte/deedbox/actions/runs/39569378057) | `168d306` | 1038 | 412 |
| [2026-10-21](https://github.com/alternayte/deedbox/actions/runs/39691059143) | `168d306` | 841 | 376 |

The spread belongs to the run, not to one code path: a run is fast or slow in every cell of a provider at once. Against the median of these seven runs, a Postgres run sits between 0.82 and 1.29, and a SQL Server run between 0.71 and 1.52. The worst single cell falls 21.8% below its median on Postgres, and 51.6% on SQL Server. Each run publishes its own table and JSON as the `benchmarks` artifact of the nightly workflow. Read that artifact for the numbers of one run.

These numbers compare releases on the same runner type. They are not a production sizing guide.

## The nightly gate

`bench/baseline.json` holds the median appends/s of each cell over those seven runs, with the ID and the commit of every run it was sampled from. The nightly job prints a `REGRESSION` line and fails when a cell's appends/s falls more than the band of its provider below that median: 40% on Postgres, 65% on SQL Server. Each band is wider than the worst drop measured on unchanged code, so a red benchmark job reports a regression and not runner variance. `just bench` applies the same bands on your own machine, against numbers that a runner measured.
