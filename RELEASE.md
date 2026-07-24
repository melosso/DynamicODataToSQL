## Releasing

Versions come from git tags via MinVer. Push a `v`-prefixed tag and the `publish.yml` workflow packs and pushes `Melosso.DynamicODataToSQL` to NuGet.

```bash
# New release: tag a version, push to trigger publish
git tag v1.0.0
git push origin v1.0.0

# Version bump: tag the next version to ship changes
git tag v1.0.1
git push origin v1.0.1
```
