# `eve-esi` quick reference

Run `eve-esi help --json` for the machine-readable catalogue.

Character commands require `--character <id|exact-name>` unless `--all` is
supported. Collection commands require `--limit` (maximum 200).

```sh
eve-esi universe type --id 34 --json
eve-esi universe system --id 30000142 --json
eve-esi universe route --from 30000142 --to 30002187 --json
eve-esi market prices --type 34 --json
eve-esi market orders --region 10000002 --type 34 --limit 20 --json
eve-esi market history --region 10000002 --type 34 --limit 30 --json
```
