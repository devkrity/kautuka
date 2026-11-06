/* Offline, bounded container workload for Gate 1 cgroup feasibility tests only. */
#define _POSIX_C_SOURCE 200809L
#include <errno.h>
#include <arpa/inet.h>
#include <netdb.h>
#include <sys/socket.h>
#include <sys/time.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

static int field(const char *path, char *value, size_t capacity)
{
    FILE *stream = fopen(path, "r");
    if (stream == NULL) return -1;
    char *line = fgets(value, (int)capacity, stream);
    fclose(stream);
    if (line == NULL) return -1;
    value[strcspn(value, "\r\n")] = 0;
    return 0;
}

static double seconds(clockid_t clock)
{
    struct timespec time;
    if (clock_gettime(clock, &time) != 0) exit(2);
    return (double)time.tv_sec + (double)time.tv_nsec / 1000000000.0;
}

static unsigned long long counter(const char *path, const char *key)
{
    FILE *stream = fopen(path, "r");
    if (stream == NULL) exit(2);
    char name[128];
    unsigned long long value;
    while (fscanf(stream, "%127s %llu", name, &value) == 2) {
        if (strcmp(name, key) == 0) { fclose(stream); return value; }
    }
    fclose(stream);
    exit(2);
}

static int expected(const char *path, const char *wanted)
{
    char value[128];
    return field(path, value, sizeof(value)) == 0 && strcmp(value, wanted) == 0;
}

int main(int argc, char **argv)
{
    if (argc == 4 && strcmp(argv[1], "dns4") == 0) {
        unsigned char query[512] = {0x4b, 0x54, 1, 0, 0, 1};
        size_t offset = 12;
        const char *label = argv[3];
        while (*label) {
            const char *end = strchr(label, '.');
            size_t length = end ? (size_t)(end - label) : strlen(label);
            if (length == 0 || length > 63 || offset + length + 6 > sizeof(query)) return 2;
            query[offset++] = (unsigned char)length;
            memcpy(query + offset, label, length); offset += length;
            if (!end) break;
            label = end + 1;
        }
        query[offset++] = 0; query[offset++] = 0; query[offset++] = 1; query[offset++] = 0; query[offset++] = 1;
        int descriptor = socket(AF_INET, SOCK_DGRAM, 0);
        if (descriptor < 0) return 2;
        struct timeval timeout = { .tv_sec = 1 };
        struct sockaddr_in server = { .sin_family = AF_INET, .sin_port = htons(53) };
        if (inet_pton(AF_INET, argv[2], &server.sin_addr) != 1
                || setsockopt(descriptor, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout)) != 0
                || connect(descriptor, (struct sockaddr *)&server, sizeof(server)) != 0) { close(descriptor); return 2; }
        unsigned char reply[1233];
        ssize_t count = -1;
        if (send(descriptor, query, offset, 0) == (ssize_t)offset) count = recv(descriptor, reply, sizeof(reply), 0);
        int received = count >= 12 && reply[0] == 0x4b && reply[1] == 0x54;
        printf("{\"dnsResponse\":%s}\n", received ? "true" : "false");
        close(descriptor);
        return received ? 0 : 3;
    }
    if (argc == 3 && (strcmp(argv[1], "lookup4") == 0 || strcmp(argv[1], "lookup6") == 0
            || strcmp(argv[1], "lookup-hold4") == 0)) {
        struct addrinfo hints = {0}, *addresses = NULL;
        int ipv6 = strcmp(argv[1], "lookup6") == 0;
        hints.ai_family = ipv6 ? AF_INET6 : AF_INET;
        hints.ai_socktype = SOCK_STREAM;
        /* Explicit family, no AI_ADDRCONFIG: AAAA must not depend on host interfaces. */
        int status = getaddrinfo(argv[2], NULL, &hints, &addresses);
        if (status != 0) { printf("{\"lookupError\":%d}\n", status); return 3; }
        printf("{\"family\":%d,\"addresses\":[", ipv6 ? 6 : 4);
        unsigned count = 0;
        for (struct addrinfo *item = addresses; item != NULL && count < 16; item = item->ai_next) {
            char text[INET6_ADDRSTRLEN];
            if (item->ai_family != hints.ai_family) { freeaddrinfo(addresses); return 2; }
            const void *address = ipv6 ? (const void *)&((struct sockaddr_in6 *)item->ai_addr)->sin6_addr
                : (const void *)&((struct sockaddr_in *)item->ai_addr)->sin_addr;
            if (inet_ntop(hints.ai_family, address, text, sizeof(text)) == NULL) { freeaddrinfo(addresses); return 2; }
            printf("%s\"%s\"", count++ ? "," : "", text);
        }
        puts("]}");
        freeaddrinfo(addresses);
        fflush(stdout);
        if (strcmp(argv[1], "lookup-hold4") == 0) sleep(8);
        return count ? 0 : 2;
    }
    if (argc == 3 && strcmp(argv[1], "artifact") == 0) { puts(argv[2]); return 0; }
    if (argc == 3 && strncmp(argv[1], "echo-", 5) == 0) {
        int ipv6 = strstr(argv[1], "6") != NULL;
        int tcp = strstr(argv[1], "tcp") != NULL;
        int descriptor = socket(ipv6 ? AF_INET6 : AF_INET, tcp ? SOCK_STREAM : SOCK_DGRAM, 0);
        if (descriptor < 0) return 2;
        struct timeval timeout = { .tv_sec = 2 };
        if (setsockopt(descriptor, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout)) != 0
            || setsockopt(descriptor, SOL_SOCKET, SO_SNDTIMEO, &timeout, sizeof(timeout)) != 0) return 2;
        struct sockaddr_in v4 = { .sin_family = AF_INET, .sin_port = htons(20199) };
        struct sockaddr_in6 v6 = { .sin6_family = AF_INET6, .sin6_port = htons(20199) };
        if (inet_pton(AF_INET, "10.231.252.2", &v4.sin_addr) != 1
            || inet_pton(AF_INET6, "fd7a:6b74:252::2", &v6.sin6_addr) != 1) return 2;
        if (connect(descriptor, ipv6 ? (struct sockaddr *)&v6 : (struct sockaddr *)&v4,
                    ipv6 ? sizeof(v6) : sizeof(v4)) != 0) { close(descriptor); return 3; }
        char request[128], response[128] = {0};
        int length = snprintf(request, sizeof(request), "%s%s", argv[2], tcp ? "\n" : "");
        if (length <= 0 || (size_t)length >= sizeof(request)) return 2;
        if (send(descriptor, request, (size_t)length, MSG_NOSIGNAL) != length) { close(descriptor); return 3; }
        size_t received = 0;
        while (received < (size_t)length) {
            ssize_t count = recv(descriptor, response + received, sizeof(response) - received, 0);
            if (count <= 0) break;
            received += (size_t)count;
            if (!tcp) break;
        }
        int success = received == (size_t)length && memcmp(request, response, received) == 0;
        printf("{\"echoed\":%s}\n", success ? "true" : "false");
        if (success) { fflush(stdout); sleep(4); }
        close(descriptor);
        return success ? 0 : 3;
    }
    if (argc != 2) return 2;
    if (strcmp(argv[1], "hold") == 0) { sleep(10); return 0; }
    if (strcmp(argv[1], "limits") == 0) {
        char memory[128], pids[128], cpu[128];
        if (field("/sys/fs/cgroup/memory.max", memory, sizeof(memory)) != 0
            || field("/sys/fs/cgroup/pids.max", pids, sizeof(pids)) != 0
            || field("/sys/fs/cgroup/cpu.max", cpu, sizeof(cpu)) != 0) return 2;
        printf("{\"uid\":%lu,\"memory_max\":\"%s\",\"pids_max\":\"%s\",\"cpu_max\":\"%s\"}\n",
            (unsigned long)getuid(), memory, pids, cpu);
        return 0;
    }
    if (strcmp(argv[1], "memory") == 0) {
        /* Bounded below the parent unit's ceiling even if the container limit fails. */
        if (!expected("/sys/fs/cgroup/memory.max", "33554432")) return 2;
        const size_t bytes = 96 * 1024 * 1024;
        volatile unsigned char *memory = malloc(bytes);
        if (memory == NULL) return 3;
        for (size_t offset = 0; offset < bytes; offset += 4096) memory[offset] = 1;
        free((void *)memory);
        puts("memory limit ineffective: touched 96 MiB");
        return 3;
    }
    if (strcmp(argv[1], "pids") == 0) {
        if (!expected("/sys/fs/cgroup/pids.max", "16")) return 2;
        unsigned long long before = counter("/sys/fs/cgroup/pids.events", "max");
        pid_t children[32];
        size_t created = 0;
        int blocked = 0;
        while (created < 32) {
            pid_t child = fork();
            if (child == 0) {
                for (;;) pause();
            }
            if (child < 0) {
                blocked = errno == EAGAIN;
                break;
            }
            children[created++] = child;
        }
        for (size_t index = 0; index < created; index++) kill(children[index], SIGTERM);
        for (size_t index = 0; index < created; index++) waitpid(children[index], NULL, 0);
        unsigned long long hits = counter("/sys/fs/cgroup/pids.events", "max") - before;
        printf("{\"created\":%lu,\"blocked\":%s,\"limit_hits\":%llu}\n", (unsigned long)created, blocked ? "true" : "false", hits);
        return blocked && created == 15 && hits > 0 ? 0 : 3;
    }
    if (strcmp(argv[1], "cpu") == 0) {
        if (!expected("/sys/fs/cgroup/cpu.max", "25000 100000")) return 2;
        unsigned long long before_count = counter("/sys/fs/cgroup/cpu.stat", "nr_throttled");
        unsigned long long before_usec = counter("/sys/fs/cgroup/cpu.stat", "throttled_usec");
        double wall = seconds(CLOCK_MONOTONIC);
        double cpu = seconds(CLOCK_PROCESS_CPUTIME_ID);
        volatile unsigned long work = 1;
        while (seconds(CLOCK_MONOTONIC) - wall < 2.0) work = work * 33 + 7;
        (void)work;
        cpu = seconds(CLOCK_PROCESS_CPUTIME_ID) - cpu;
        wall = seconds(CLOCK_MONOTONIC) - wall;
        printf("{\"wall_seconds\":%.3f,\"cpu_seconds\":%.3f}\n", wall, cpu);
        unsigned long long count = counter("/sys/fs/cgroup/cpu.stat", "nr_throttled") - before_count;
        unsigned long long usec = counter("/sys/fs/cgroup/cpu.stat", "throttled_usec") - before_usec;
        printf("{\"throttled_periods\":%llu,\"throttled_usec\":%llu}\n", count, usec);
        return wall >= 1.8 && cpu < 1.2 && count > 0 && usec > 0 ? 0 : 3;
    }
    return 2;
}
