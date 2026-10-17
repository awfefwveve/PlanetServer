# 데이터베이스 설계 (PostgreSQL)

코드: `src/Server/PostgresStore.cs` · 테스트: `tests/Program.cs`의 `PostgreSQL ...` 항목 (`PLANET_TEST_DB` 설정 시 실행)

## 테이블 5개

```
accounts ─┬─< characters ─┬─< inventory_items   (가방 칸 하나 = 한 줄)
          │               ├─< equipment         (입은 장비 하나 = 한 줄)
          │               └─< character_skills  (배운 스킬 하나 = 한 줄)
```

| 테이블 | 기본키 | 들어있는 것 |
|---|---|---|
| `accounts` | `account_key` (소문자 로그인 이름) | 이름, 비밀번호 해시·솔트, `revision`, 생성·수정 시각 |
| `characters` | (`account_key`, `character_id`) | 외형, 지역·좌표, 처치 수, 골드, 레벨·경험치·HP·MP, 스킬 포인트 |
| `inventory_items` | (`account_key`, `character_id`, `slot`) | 아이템 ID, 개수 |
| `equipment` | (`account_key`, `character_id`, `equip_slot`) | 아이템 ID |
| `character_skills` | (`account_key`, `character_id`, `skill_id`) | 스킬 레벨 |

## 왜 이렇게 나눴나

- **아이템이 한 줄씩 있어야 운영이 된다.** "이 아이템 누가 몇 개 갖고 있지?"(복사 버그 추적), "골드 많은 순 캐릭터" 같은 질문을 SQL 한 줄로 바로 답할 수 있다. 계정 하나를 JSON 한 덩어리로 넣으면 이런 질문마다 전체를 열어봐야 한다.
- **DB가 규칙을 한 번 더 지킨다.** `count > 0`, `gold >= 0`, 칸 번호 중복 불가(기본키), 캐릭터 이름 중복 불가(`name_key UNIQUE`). 서버 코드에 버그가 있어도 이상한 값은 저장 단계에서 막힌다.
- **캐릭터를 지우면 딸린 줄이 같이 지워진다.** 자식 테이블은 `ON DELETE CASCADE`라 주인 없는 아이템이 남지 않는다.

## 인덱스

| 인덱스 | 이유 |
|---|---|
| 각 테이블 기본키 | 로그인할 때 "이 계정의 캐릭터/가방"을 찾는 조회. 기본키가 `account_key`로 시작해서 별도 인덱스가 필요 없다 |
| `characters.name_key` (UNIQUE) | 캐릭터 생성 때 이름 중복 확인, 그리고 동시에 같은 이름으로 만들려는 경쟁을 DB가 막음 |
| `inventory_items(item_id)` | 운영 조회 "아이템 X를 가진 사람"(복사·악용 감사) |

필요 없는 인덱스는 저장할 때마다 같이 갱신돼야 해서 일부러 더 만들지 않았다.

## 트랜잭션과 동시성

- **저장 한 번 = 트랜잭션 하나.** 계정 revision 확인 → 캐릭터 줄 → 아이템 줄이 전부 성공해야 커밋된다. 중간에 서버가 죽으면 전부 없던 일이 된다.
- **revision (낙관적 잠금).** `UPDATE accounts ... WHERE revision = 기대값`. 다른 저장이 먼저 끝났으면 0줄이 바뀌고 저장을 거부한다. 오래된 상태가 최신 보상을 덮어쓰지 못한다.
- **거래는 두 계정을 한 트랜잭션에.** `SaveMany`가 두 계정을 같은 트랜잭션에서 쓴다. 한쪽 revision이 틀리면 양쪽 다 롤백 → 아이템이 사라지거나 복사되지 않는다.
- **한 번에 보내기.** 한 계정의 줄들은 `NpgsqlBatch`로 묶어 DB와 한 번만 주고받는다.

## 저장 방식의 선택 (면접에서 물어볼 만한 것)

저장할 때 그 계정의 캐릭터 줄을 지우고 다시 넣는다(자식 줄은 CASCADE로 같이). 바뀐 칸만 고르는 것보다 단순하고, 트랜잭션 안이라 다른 사람에게 중간 상태가 보이지 않는다. 계정당 줄이 수십 개라 지금 규모에선 충분하다. 동접이 늘어 저장이 병목이 되면 바뀐 캐릭터·칸만 쓰는 방식(차분 저장)으로 바꾸는 게 다음 단계다.

## 예전 데이터 이관

이전 버전은 계정을 `planet_accounts` 테이블에 jsonb 한 덩어리로 저장했다. 서버가 시작할 때(`Initialize`) 새 테이블에 없는 계정만 골라 옮긴다. 두 번 실행해도 안전하고, 옛 테이블은 백업으로 그대로 둔다.

## 이번에 같이 고친 버그

로그인은 이름을 소문자로 바꿔 찾는데(`World.Login`), 파일 저장소는 받은 이름 그대로 파일명을 만들었다. 윈도우는 대소문자를 구분하지 않아서 안 보였고, 리눅스에서 테스트를 돌리자 `transportTester` 계정을 못 찾아 실패했다. 이제 두 저장소 모두 소문자 이름으로 찾는다.

## 써볼 만한 조회

```sql
-- 아이템별 총 보유량 (복사 버그 감시)
SELECT item_id, sum(count) FROM inventory_items GROUP BY item_id ORDER BY 2 DESC;

-- 골드 상위 10명
SELECT name, level, gold FROM characters WHERE name_key IS NOT NULL ORDER BY gold DESC LIMIT 10;

-- 한 캐릭터의 가방
SELECT slot, item_id, count FROM inventory_items i JOIN characters c USING (account_key, character_id)
WHERE c.name_key = upper('나린') ORDER BY slot;
```
