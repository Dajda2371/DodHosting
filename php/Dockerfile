FROM php:8.3-fpm-alpine
# pdo_sqlite and sqlite3 ship with the base image; enough for counters and small guestbooks.
COPY php.ini /usr/local/etc/php/conf.d/zz-dod.ini
COPY www.conf /usr/local/etc/php-fpm.d/zz-dod.conf
# php-fpm logs to stderr; pid file must live in the tmpfs because the root fs is read-only
RUN sed -i 's|^;*pid = .*|pid = /tmp/php-fpm.pid|' /usr/local/etc/php-fpm.conf
USER www-data
