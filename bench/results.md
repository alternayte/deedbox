# Deedbox benchmarks

Append throughput and latency across the matrix: writers × events per append × provider × mode. Each cell runs 2 s of warm-up, then 8 s of measurement. Each writer appends to its own stream, so the position counter is the only point of contention. Counter p50 and p99 are the time from the counter update, including any wait for its lock, to the end of the append.

- Run: nightly workflow, GitHub-hosted `ubuntu-latest` runner, Postgres 17 and SQL Server 2022 in Docker on the same machine.
- Commit: `0d48a6d`, 2026-09-24.
- Modes: `neither` means Deedbox opens and commits the transaction; `efcore` means `UseDbContext` with one inline EF Core projection.

| Provider | Mode | Writers | Events per append | Appends/s | Events/s | p50 ms | p99 ms | Counter p50 ms | Counter p99 ms |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| postgres | neither | 1 | 1 | 567 | 567 | 1.71 | 2.44 | 0.89 | 1.34 |
| postgres | neither | 8 | 1 | 1167 | 1167 | 6.16 | 17.75 | 4.71 | 16.29 |
| postgres | neither | 32 | 1 | 914 | 914 | 21.98 | 170.95 | 20.43 | 169.64 |
| postgres | neither | 128 | 1 | 674 | 674 | 132.09 | 887.02 | 129.90 | 885.97 |
| postgres | neither | 1 | 10 | 552 | 5520 | 1.81 | 1.98 | 0.98 | 1.11 |
| postgres | neither | 8 | 10 | 978 | 9782 | 7.19 | 22.24 | 5.66 | 20.58 |
| postgres | neither | 32 | 10 | 797 | 7974 | 25.80 | 197.80 | 24.31 | 196.22 |
| postgres | neither | 128 | 10 | 610 | 6097 | 144.19 | 998.64 | 142.56 | 997.45 |
| postgres | efcore | 1 | 1 | 287 | 287 | 3.42 | 4.39 | 0.91 | 1.18 |
| postgres | efcore | 8 | 1 | 733 | 733 | 10.70 | 17.36 | 4.31 | 10.31 |
| postgres | efcore | 32 | 1 | 608 | 608 | 33.84 | 251.41 | 27.18 | 245.26 |
| postgres | efcore | 128 | 1 | 445 | 445 | 187.96 | 1397.83 | 180.37 | 1383.13 |
| postgres | efcore | 1 | 10 | 267 | 2668 | 3.73 | 4.10 | 1.07 | 1.23 |
| postgres | efcore | 8 | 10 | 652 | 6520 | 11.94 | 20.67 | 5.10 | 12.84 |
| postgres | efcore | 32 | 10 | 557 | 5568 | 38.40 | 266.68 | 31.67 | 261.06 |
| postgres | efcore | 128 | 10 | 437 | 4374 | 195.45 | 1460.19 | 189.39 | 1453.89 |
| sqlserver | neither | 1 | 1 | 375 | 375 | 2.57 | 4.96 | 1.38 | 3.72 |
| sqlserver | neither | 8 | 1 | 697 | 697 | 11.02 | 19.27 | 9.59 | 17.72 |
| sqlserver | neither | 32 | 1 | 699 | 699 | 45.27 | 55.17 | 43.80 | 53.79 |
| sqlserver | neither | 128 | 1 | 635 | 635 | 184.10 | 408.39 | 182.01 | 405.10 |
| sqlserver | neither | 1 | 10 | 353 | 3527 | 2.74 | 5.15 | 1.53 | 3.91 |
| sqlserver | neither | 8 | 10 | 560 | 5601 | 13.03 | 52.78 | 11.51 | 51.29 |
| sqlserver | neither | 32 | 10 | 561 | 5610 | 54.62 | 101.20 | 53.16 | 99.56 |
| sqlserver | neither | 128 | 10 | 505 | 5054 | 120.73 | 359.90 | 118.82 | 340.92 |
| sqlserver | efcore | 1 | 1 | 208 | 208 | 4.63 | 7.76 | 1.40 | 3.97 |
| sqlserver | efcore | 8 | 1 | 517 | 517 | 15.28 | 19.80 | 10.15 | 14.91 |
| sqlserver | efcore | 32 | 1 | 471 | 471 | 64.09 | 233.16 | 58.89 | 228.84 |
| sqlserver | efcore | 128 | 1 | 483 | 483 | 205.54 | 1389.85 | 199.45 | 226.35 |
| sqlserver | efcore | 1 | 10 | 203 | 2032 | 4.81 | 6.92 | 1.56 | 3.48 |
| sqlserver | efcore | 8 | 10 | 447 | 4468 | 17.16 | 28.01 | 12.03 | 22.48 |
| sqlserver | efcore | 32 | 10 | 436 | 4359 | 71.38 | 160.45 | 66.12 | 156.92 |
| sqlserver | efcore | 128 | 10 | 405 | 4053 | 148.37 | 2498.75 | 141.60 | 351.52 |

## Reading the numbers

- Appends serialize on the position counter from its update to commit, so throughput peaks at a few writers and falls slowly as more writers queue. The peak is about 1,170 appends/s on Postgres and 700 on SQL Server on this runner.
- More events per append cost little: 10 events per append move about 10 times the events at a similar append rate.
- With many writers, latency is almost all counter wait: counter p50 is close to append p50.
- A shared CI runner varies from run to run. These numbers compare releases on the same runner type; they are not a production sizing guide.

The nightly job fails when a cell's appends/s falls more than 30% below `bench/baseline.json`.
