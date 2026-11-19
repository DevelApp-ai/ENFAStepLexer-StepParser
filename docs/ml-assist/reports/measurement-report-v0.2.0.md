---
layout: default
title: ML-Assist Measurement Report v0.2.0
---

# ML-Assist Measurement Report - v0.2.0 (issues #93 / #58)

Recorded: 2026-11-18 (UTC) - schema `ml-assist-report/1`
Corpus: Minotaur-Grammars `main`
Trainer: train.py 1.1 (newton-l2, streaming)
Records: 41 (grammar, example) pairs, parsed twice - ML off (shipped default) vs ML on (v0.2.0 artifacts).

## Artifacts

| Artifact | modelVersion | holdout records | holdout log-loss | holdout accuracy | holdout AUC |
|---|---|---|---|---|---|
| `rule-prioritizer.json` | 0.2.0 | 94881 | 0.060098 | 0.981598 | 0.924777 |
| `path-pruner.json` | 0.2.0 | 1140 | 0.317474 | 0.892105 | 0.691561 |

Path pruner gates: `confidenceThreshold` = 0.95, `minPathCount` = 10.

## Zero semantic change

`successOff == successOn` for **all 41 records**: **0 semantic changes** (asserted by `MlAssistEvaluationTests`, which fails the run otherwise; this report re-verifies and exits non-zero on any divergence).

## End-to-end measurement

| Arm | Parses | Successes | p50 (us) | p95 (us) | Total (us) | Wasted-attempt ratio (mean) |
|---|---|---|---|---|---|---|
| ML off (shipped default) | 41 | 10 | 2579.3 | 122462.8 | 894615.1 | 0.5820 |
| ML on (v0.2.0) | 41 | 10 | 3174.8 | 157839.3 | 1182526.8 | 0.3390 |

- Parse time (total): 894615.1 -> 1182526.8 us (**32.18%**) (negative = faster)
- Wasted-attempt ratio (mean): 0.5820 -> 0.3390 (**-41.76%**) (negative = better)
- Examples faster with ML: **0**, slower: **41**, unchanged: 0
- Paths pruned: **14** - pruner consultations: **951**

## Per-grammar changes (total parse time)

| Grammar | Records | ML off total (us) | ML on total (us) | Change |
|---|---|---|---|---|
| `natural-languages/icelandic/Icelandic.grammar` | 1 | 180418.8 | 200272.7 | +11.00% |
| `infrastructure/makefile/Makefile.grammar` | 1 | 152514.3 | 157839.3 | +3.49% |
| `natural-languages/norwegian-bokmal/Norwegian_Bokmål.grammar` | 1 | 122462.8 | 146228.4 | +19.41% |
| `natural-languages/norwegian-nynorsk/Norwegian_Nynorsk.grammar` | 1 | 66638.1 | 71119.9 | +6.73% |
| `benchmarks/synthetic/rules-320/rules-320.grammar` | 1 | 59881.8 | 78717.5 | +31.45% |
| `natural-languages/faroese/Faroese.grammar` | 1 | 57027.4 | 63690.5 | +11.68% |
| `data-query/gremlin/Gremlin.grammar` | 1 | 51440.3 | 182840.4 | +255.44% |
| `ci-cd/bitbucket-pipelines/BitbucketPipelines.grammar` | 1 | 36147.6 | 68457.3 | +89.38% |
| `specialized/labyrinth/Labyrinth.grammar` | 1 | 33988.7 | 37991.8 | +11.78% |
| `benchmarks/synthetic/rules-180/rules-180.grammar` | 1 | 33650.2 | 43522.5 | +29.34% |
| `data-config/flatbuffers/FlatBuffers.grammar` | 1 | 20693.2 | 29363.0 | +41.90% |
| `networking/bind9-zone-file/Bind9ZoneFile.grammar` | 1 | 17431.3 | 21834.1 | +25.26% |
| `benchmarks/synthetic/rules-060/rules-060.grammar` | 1 | 13772.7 | 16990.8 | +23.37% |
| `data-config/protobuf/Protobuf.grammar` | 1 | 6579.4 | 7844.9 | +19.23% |
| `benchmarks/synthetic/rules-020/rules-020.grammar` | 1 | 6222.0 | 7110.2 | +14.28% |
| `networking/haproxy-config/HAProxyConfig.grammar` | 1 | 5763.6 | 7508.8 | +30.28% |
| `ci-cd/jenkinsfile/Jenkinsfile.grammar` | 1 | 5159.8 | 7103.4 | +37.67% |
| `data-config/yaml/YAML.grammar` | 1 | 5087.1 | 5727.5 | +12.59% |
| `ci-cd/gitlab-ci/GitLabCI.grammar` | 1 | 3994.2 | 6482.2 | +62.29% |
| `data-config/ini/INI.grammar` | 1 | 2595.8 | 3131.0 | +20.62% |
| `networking/nginx-config/NginxConfig.grammar` | 1 | 2579.3 | 3174.8 | +23.09% |
| `natural-languages/finnish/Finnish.grammar` | 1 | 2184.8 | 3742.5 | +71.30% |
| `specification-first/ai-spec/AiSpec.grammar` | 1 | 1228.6 | 1586.7 | +29.15% |
| `infrastructure/ansible-playbook/AnsiblePlaybook.grammar` | 1 | 1165.1 | 1279.3 | +9.80% |
| `specification-first/model-spec/ModelSpec.grammar` | 1 | 1058.6 | 1355.3 | +28.03% |
| `networking/envoy-config/EnvoyConfig.grammar` | 1 | 679.0 | 1128.6 | +66.22% |
| `infrastructure/cloudformation/CloudFormation.grammar` | 1 | 645.1 | 745.8 | +15.61% |
| `networking/apache-httpd-config/ApacheHttpdConfig.grammar` | 1 | 555.6 | 715.6 | +28.80% |
| `data-config/avro/Avro.grammar` | 1 | 526.2 | 587.7 | +11.69% |
| `programming-languages/sample/sample.grammar` | 1 | 494.3 | 945.8 | +91.34% |
| `natural-languages/swedish-expanded/Swedish_Expanded.grammar` | 1 | 471.7 | 1008.6 | +113.82% |
| `ci-cd/circleci-config/CircleCIConfig.grammar` | 1 | 431.4 | 590.8 | +36.95% |
| `natural-languages/old-norse/Old_Norse.grammar` | 1 | 409.8 | 691.3 | +68.69% |
| `data-query/cypher/Cypher.grammar` | 1 | 312.1 | 435.1 | +39.41% |
| `natural-languages/greenlandic/Greenlandic.grammar` | 1 | 240.6 | 453.3 | +88.40% |
| `natural-languages/swedish/Swedish.grammar` | 1 | 106.1 | 206.7 | +94.82% |
| `system-output/crontab/Crontab.grammar` | 1 | 15.4 | 31.2 | +102.60% |
| `natural-languages/lithuanian/Lithuanian.grammar` | 1 | 11.7 | 19.8 | +69.23% |
| `natural-languages/irish/Irish.grammar` | 1 | 11.2 | 18.9 | +68.75% |
| `natural-languages/latvian/Latvian.grammar` | 1 | 10.1 | 18.2 | +80.20% |
| `natural-languages/maltese/Maltese.grammar` | 1 | 9.3 | 14.6 | +56.99% |

## Acceptance (#58 definition of done)

- Zero semantic changes: **PASS**
- End-to-end improvement (parse time and/or wasted-attempt ratio): **PASS**
- Overall verdict: **PASS**

Timings are machine-dependent and intended for relative, same-run comparison only (both arms measured in the same process; see [ml-baseline/README.md](../ml-baseline/README.md) for the pre-ML #73 baseline and its caveats).

