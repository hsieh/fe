\ ===== lang-sql.fs — SQL syntax rules =====
create sql-kwbuf 1024 allot  variable sql-kwlen 0 sql-kwlen !
: sql-init
  s"  SELECT FROM WHERE INSERT INTO VALUES UPDATE SET DELETE CREATE TABLE DROP ALTER INDEX VIEW JOIN LEFT RIGHT INNER OUTER GROUP BY ORDER HAVING LIMIT AND OR NOT NULL AS ON DISTINCT UNION ALL PRIMARY KEY FOREIGN " sql-kwbuf 1024 sql-kwlen kw-put ;
sql-init
create sql-dash    45 c, 45 c,       \ --
create sql-dslash* 47 c, 42 c,       \ /*
create sql-*slash  42 c, 47 c,       \ */
create sql-sq      39 c,             \ '
: sql-kw$  sql-kwbuf sql-kwlen @ ;
: rules-sql
  rules-clear
  sql-kw$                 4 rule-kw
                          3 rule-num
  sql-dslash* 2 sql-*slash 2  1 rule-range
  sql-dash 2  0 0             1 rule-range
  sql-sq 1  sql-sq 1          2 rule-range
                          0 rule-word ;
: register-language-sql  s" sql" ['] rules-sql register-lang  s" sql" ['] rules-sql register-lang-name ;
register-language-sql
